using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityMcpPlugin
{
    internal enum EditorBridgeState
    {
        Ready,
        Compiling,
        Reloading,
    }

    internal enum UnityMcpJobState
    {
        Queued,
        Running,
        Succeeded,
        Failed,
        Timeout,
        Cancelled,
    }

    internal enum PortReconfigureStatus
    {
        Applied,
        RolledBack,
        Failed,
    }

    internal readonly struct PortReconfigureResult
    {
        internal PortReconfigureResult(PortReconfigureStatus status, int activePort, string errorCode, string errorMessage)
        {
            Status = status;
            ActivePort = activePort;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        internal PortReconfigureStatus Status { get; }
        internal int ActivePort { get; }
        internal string ErrorCode { get; }
        internal string ErrorMessage { get; }

        internal static PortReconfigureResult Applied(int activePort)
        {
            return new PortReconfigureResult(PortReconfigureStatus.Applied, activePort, string.Empty, string.Empty);
        }

        internal static PortReconfigureResult RolledBack(int activePort, string errorCode, string errorMessage)
        {
            return new PortReconfigureResult(PortReconfigureStatus.RolledBack, activePort, errorCode, errorMessage);
        }

        internal static PortReconfigureResult Failed(int activePort, string errorCode, string errorMessage)
        {
            return new PortReconfigureResult(PortReconfigureStatus.Failed, activePort, errorCode, errorMessage);
        }
    }

    [InitializeOnLoad]
    internal static class UnityMcpPluginBootstrap
    {
        static UnityMcpPluginBootstrap()
        {
            var runtime = UnityMcpPluginRuntime.Instance;
            runtime.Initialize();

            CompilationPipeline.compilationStarted += _ => runtime.PublishEditorState(EditorBridgeState.Compiling);
            CompilationPipeline.compilationFinished += _ => runtime.PublishEditorState(EditorBridgeState.Ready);
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                runtime.PublishEditorState(EditorBridgeState.Reloading);
                runtime.Shutdown();
            };

            EditorApplication.delayCall += () => runtime.PublishEditorState(EditorBridgeState.Ready);
            EditorApplication.quitting += runtime.Shutdown;
        }
    }

    internal sealed class UnityMcpPluginRuntime
    {
        private const string PluginVersion = "0.1.0";
        private const int ProtocolVersion = 1;
        private const string Host = "127.0.0.1";
        private const string UnityWsPath = "/unity";
        private const int MaxMessageBytes = 1024 * 1024;

        private const int ReconnectInitialMs = 100;
        private const double ReconnectMultiplier = 1.7;
        private const int ReconnectMaxBackoffMs = 1200;
        private const double ReconnectJitterRatio = 0.1;

        private const int ReconfigureConnectTimeoutMs = 5000;
        private const int ReconfigureMaxAttempts = 3;
        private const int ReconfigureRetryIntervalMs = 200;

        private readonly object _gate = new object();
        private readonly object _stateGate = new object();
        private readonly object _connectedEventGate = new object();
        private readonly object _socketGate = new object();
        private readonly object _jobGate = new object();

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _reconfigureLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _reconnectSignal = new SemaphoreSlim(0, int.MaxValue);

        private readonly Dictionary<string, JobRecord> _jobs = new Dictionary<string, JobRecord>(StringComparer.Ordinal);
        private readonly System.Random _random = new System.Random();

        private CancellationTokenSource _lifecycleCts;
        private CancellationTokenSource _sessionCts;
        private Task _lifecycleTask;

        private int _desiredPort;
        private int _activePort;
        private int _connectedPort = -1;

        private bool _initialized;
        private bool _shuttingDown;

        private ClientWebSocket _socket;

        private EditorBridgeState _editorState = EditorBridgeState.Ready;
        private ulong _editorSeq;

        private event Action<int> ConnectedPortChanged;

        private UnityMcpPluginRuntime()
        {
            _lifecycleTask = Task.CompletedTask;
            _lifecycleCts = new CancellationTokenSource();
        }

        internal static UnityMcpPluginRuntime Instance { get; } = new UnityMcpPluginRuntime();

        internal void Initialize()
        {
            lock (_gate)
            {
                if (_initialized)
                {
                    return;
                }

                var settings = UnityMcpPluginSettings.instance;
                var validation = settings.Validate();
                if (!validation.IsValid)
                {
                    LogError(
                        "Plugin initialization aborted by invalid settings",
                        ("code", validation.Code),
                        ("message", validation.Message));
                    return;
                }

                _desiredPort = settings.port;
                _activePort = settings.port;
                _shuttingDown = false;

                UnityMcpLogBuffer.Initialize();
                UnityMcpMainThreadDispatcher.Initialize();

                _lifecycleCts = new CancellationTokenSource();
                _lifecycleTask = Task.Run(() => LifecycleLoopAsync(_lifecycleCts.Token));

                _initialized = true;

                LogInfo("Unity MCP plugin initialized", ("port", _desiredPort));
            }
        }

        internal void Shutdown()
        {
            lock (_gate)
            {
                if (!_initialized || _shuttingDown)
                {
                    return;
                }

                _shuttingDown = true;
            }

            try
            {
                _lifecycleCts.Cancel();
            }
            catch
            {
                // no-op
            }

            RequestReconnect();
            CancelCurrentSession();

            try
            {
                _lifecycleTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // no-op
            }

            SafeDisposeSocket();

            lock (_gate)
            {
                _initialized = false;
                _shuttingDown = false;
            }

            LogInfo("Unity MCP plugin stopped");
        }

        internal int GetActivePort()
        {
            lock (_gate)
            {
                if (!_initialized)
                {
                    return UnityMcpPluginSettings.instance.port;
                }

                return _activePort;
            }
        }

        internal string GetRuntimeSummary()
        {
            int desiredPort;
            int connectedPort;
            EditorBridgeState state;
            ulong seq;

            lock (_gate)
            {
                if (!_initialized)
                {
                    return "stopped";
                }

                desiredPort = _desiredPort;
                connectedPort = _connectedPort;
            }

            lock (_stateGate)
            {
                state = _editorState;
                seq = _editorSeq;
            }

            var connection = connectedPort > 0 ? "connected" : "waiting_editor";
            return $"{connection}, desired_port={desiredPort}, connected_port={connectedPort}, editor_state={ToWireState(state)}, seq={seq}";
        }

        internal void PublishEditorState(EditorBridgeState nextState)
        {
            bool changed;
            ulong seq;

            lock (_stateGate)
            {
                changed = _editorState != nextState;
                _editorState = nextState;
                if (changed)
                {
                    _editorSeq += 1;
                }

                seq = _editorSeq;
            }

            if (!changed)
            {
                return;
            }

            LogInfo("Editor state updated", ("state", ToWireState(nextState)), ("seq", seq));
            _ = TrySendEditorStatusAsync(nextState, seq);
        }

        internal async Task<PortReconfigureResult> ApplyPortChangeAsync(int newPort)
        {
            if (!_initialized)
            {
                Initialize();
            }

            if (!_initialized)
            {
                return PortReconfigureResult.Failed(
                    GetActivePort(),
                    "ERR_CONFIG_VALIDATION",
                    "plugin runtime is not initialized");
            }

            if (newPort < 1 || newPort > 65535)
            {
                return PortReconfigureResult.Failed(GetActivePort(), "ERR_INVALID_PORT", "port must be between 1 and 65535");
            }

            if (!await _reconfigureLock.WaitAsync(0))
            {
                return PortReconfigureResult.Failed(GetActivePort(), "ERR_RECONFIG_IN_PROGRESS", "reconfiguration already running");
            }

            try
            {
                int oldPort;
                lock (_gate)
                {
                    oldPort = _desiredPort;
                }

                if (newPort == oldPort)
                {
                    return PortReconfigureResult.Applied(oldPort);
                }

                lock (_gate)
                {
                    _desiredPort = newPort;
                }

                RequestReconnect();
                var connectedNewPort = await WaitForConnectedPortAsync(newPort);
                if (connectedNewPort)
                {
                    lock (_gate)
                    {
                        _activePort = newPort;
                    }

                    LogInfo("Port reconfigure applied", ("old_port", oldPort), ("new_port", newPort));
                    return PortReconfigureResult.Applied(newPort);
                }

                lock (_gate)
                {
                    _desiredPort = oldPort;
                }

                RequestReconnect();
                var rollbackSuccess = await WaitForConnectedPortAsync(oldPort);
                if (rollbackSuccess)
                {
                    lock (_gate)
                    {
                        _activePort = oldPort;
                    }

                    LogWarn("Port reconfigure rolled back", ("old_port", oldPort), ("new_port", newPort));
                    return PortReconfigureResult.RolledBack(
                        oldPort,
                        "ERR_RECONFIG_CONNECT_TIMEOUT",
                        "failed to connect to new port and rolled back to previous port");
                }

                LogError("Port reconfigure rollback failed", ("old_port", oldPort), ("new_port", newPort));
                return PortReconfigureResult.Failed(
                    GetActivePort(),
                    "ERR_RECONFIG_ROLLBACK_FAILED",
                    "failed to connect to new port and rollback port");
            }
            catch (Exception ex)
            {
                return PortReconfigureResult.Failed(GetActivePort(), "ERR_RECONFIG_IN_PROGRESS", ex.Message);
            }
            finally
            {
                _reconfigureLock.Release();
            }
        }

        private async Task LifecycleLoopAsync(CancellationToken cancellationToken)
        {
            double reconnectBackoffMs = ReconnectInitialMs;

            while (!cancellationToken.IsCancellationRequested)
            {
                var port = GetDesiredPort();
                try
                {
                    await RunSessionAsync(port, cancellationToken);
                    reconnectBackoffMs = ReconnectInitialMs;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogWarn("Bridge session ended with error", ("port", port), ("error", ex.Message));
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var delayMs = ApplyJitter(reconnectBackoffMs);
                reconnectBackoffMs = Math.Min(reconnectBackoffMs * ReconnectMultiplier, ReconnectMaxBackoffMs);

                var reconnectedImmediately = await WaitReconnectSignalOrDelayAsync(delayMs, cancellationToken);
                if (reconnectedImmediately)
                {
                    reconnectBackoffMs = ReconnectInitialMs;
                }
            }
        }

        private async Task RunSessionAsync(int port, CancellationToken lifecycleToken)
        {
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(lifecycleToken);
            lock (_gate)
            {
                _sessionCts = sessionCts;
            }

            using var socket = new ClientWebSocket();
            var uri = new Uri($"ws://{Host}:{port}{UnityWsPath}");

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token))
            {
                connectCts.CancelAfter(ReconfigureConnectTimeoutMs);
                await socket.ConnectAsync(uri, connectCts.Token);
            }

            lock (_socketGate)
            {
                _socket = socket;
            }

            lock (_gate)
            {
                _connectedPort = port;
                _activePort = port;
            }

            ResetEditorSequenceForNewSession();
            await SendHelloAsync(sessionCts.Token);
            await SendCurrentEditorStatusAsync(sessionCts.Token);

            RaiseConnectedPortChanged(port);

            LogInfo("Bridge connected", ("port", port), ("uri", uri.ToString()));

            try
            {
                await ReceiveLoopAsync(socket, sessionCts.Token);
            }
            finally
            {
                lock (_socketGate)
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        _socket = null;
                    }
                }

                lock (_gate)
                {
                    if (_connectedPort == port)
                    {
                        _connectedPort = -1;
                    }
                }

                SafeCloseSocket(socket, "session-ended");
                LogWarn("Bridge disconnected", ("port", port));
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[8 * 1024];
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (result.Count > 0)
                    {
                        stream.Write(buffer, 0, result.Count);
                    }

                    if (stream.Length > MaxMessageBytes)
                    {
                        await SendProtocolErrorAsync(null, "ERR_INVALID_REQUEST", "message too large", cancellationToken);
                        SafeCloseSocket(socket, "message-too-large");
                        return;
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var raw = Encoding.UTF8.GetString(stream.ToArray());
                await HandleIncomingMessageAsync(raw, cancellationToken);
            }
        }

        private async Task HandleIncomingMessageAsync(string raw, CancellationToken cancellationToken)
        {
            if (!(MiniJson.Deserialize(raw) is Dictionary<string, object> message))
            {
                LogWarn("Received invalid JSON message");
                return;
            }

            var protocolVersion = GetInt(message, "protocol_version");
            var requestId = GetString(message, "request_id");
            if (protocolVersion != ProtocolVersion)
            {
                await SendProtocolErrorAsync(requestId, "ERR_INVALID_REQUEST", "protocol_version mismatch", cancellationToken);
                SafeCloseCurrentSocket("protocol-version-mismatch");
                return;
            }

            var type = GetString(message, "type");
            if (string.IsNullOrEmpty(type))
            {
                await SendProtocolErrorAsync(requestId, "ERR_INVALID_REQUEST", "type is required", cancellationToken);
                return;
            }

            switch (type)
            {
                case "hello":
                    HandleServerHello(message);
                    return;
                case "capability":
                    HandleServerCapability(message);
                    return;
                case "ping":
                    await SendPongAsync(cancellationToken);
                    return;
                case "execute":
                    await HandleExecuteAsync(message, cancellationToken);
                    return;
                case "submit_job":
                    await HandleSubmitJobAsync(message, cancellationToken);
                    return;
                case "get_job_status":
                    await HandleGetJobStatusAsync(message, cancellationToken);
                    return;
                case "cancel":
                    await HandleCancelAsync(message, cancellationToken);
                    return;
                case "error":
                    LogWarn("Received error from server", ("payload", raw));
                    return;
                default:
                    await SendProtocolErrorAsync(requestId, "ERR_UNKNOWN_COMMAND", $"unknown command type: {type}", cancellationToken);
                    return;
            }
        }

        private void HandleServerHello(Dictionary<string, object> message)
        {
            var serverVersion = GetString(message, "server_version") ?? "unknown";
            LogInfo("Received server hello", ("server_version", serverVersion));
        }

        private void HandleServerCapability(Dictionary<string, object> message)
        {
            if (!message.TryGetValue("tools", out var toolsObj) || !(toolsObj is IList tools))
            {
                LogInfo("Received capability without tools");
                return;
            }

            LogInfo("Received capability", ("tool_count", tools.Count));
        }

        private async Task HandleExecuteAsync(Dictionary<string, object> message, CancellationToken cancellationToken)
        {
            var requestId = GetString(message, "request_id");
            var toolName = GetString(message, "tool_name");
            var parameters = GetDictionary(message, "params") ?? new Dictionary<string, object>();

            if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(toolName))
            {
                await SendProtocolErrorAsync(requestId, "ERR_INVALID_REQUEST", "request_id and tool_name are required", cancellationToken);
                return;
            }

            try
            {
                var result = await UnityMcpMainThreadDispatcher.InvokeAsync(() => ExecuteSyncTool(toolName, parameters));
                await SendMessageAsync(new Dictionary<string, object>
                {
                    ["type"] = "result",
                    ["protocol_version"] = ProtocolVersion,
                    ["request_id"] = requestId,
                    ["status"] = "ok",
                    ["result"] = result,
                }, cancellationToken);
            }
            catch (UnityMcpPluginException ex)
            {
                await SendMessageAsync(new Dictionary<string, object>
                {
                    ["type"] = "result",
                    ["protocol_version"] = ProtocolVersion,
                    ["request_id"] = requestId,
                    ["status"] = "error",
                    ["result"] = new Dictionary<string, object>
                    {
                        ["code"] = ex.Code,
                        ["message"] = ex.Message,
                    },
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                await SendMessageAsync(new Dictionary<string, object>
                {
                    ["type"] = "result",
                    ["protocol_version"] = ProtocolVersion,
                    ["request_id"] = requestId,
                    ["status"] = "error",
                    ["result"] = new Dictionary<string, object>
                    {
                        ["code"] = "ERR_UNITY_EXECUTION",
                        ["message"] = ex.Message,
                    },
                }, cancellationToken);
            }
        }

        private async Task HandleSubmitJobAsync(Dictionary<string, object> message, CancellationToken cancellationToken)
        {
            var requestId = GetString(message, "request_id");
            var toolName = GetString(message, "tool_name");
            var parameters = GetDictionary(message, "params") ?? new Dictionary<string, object>();

            if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(toolName))
            {
                await SendProtocolErrorAsync(requestId, "ERR_INVALID_REQUEST", "request_id and tool_name are required", cancellationToken);
                return;
            }

            if (!string.Equals(toolName, "run_tests", StringComparison.Ordinal))
            {
                await SendProtocolErrorAsync(requestId, "ERR_UNKNOWN_COMMAND", $"unsupported job tool: {toolName}", cancellationToken);
                return;
            }

            var mode = GetString(parameters, "mode") ?? "all";
            if (!string.Equals(mode, "all", StringComparison.Ordinal) &&
                !string.Equals(mode, "edit", StringComparison.Ordinal) &&
                !string.Equals(mode, "play", StringComparison.Ordinal))
            {
                await SendProtocolErrorAsync(requestId, "ERR_INVALID_PARAMS", "mode must be all|edit|play", cancellationToken);
                return;
            }

            var filter = GetString(parameters, "filter") ?? string.Empty;

            var jobId = $"job-{Guid.NewGuid():N}";
            var record = new JobRecord(jobId);
            lock (_jobGate)
            {
                _jobs[jobId] = record;
            }

            await SendMessageAsync(new Dictionary<string, object>
            {
                ["type"] = "submit_job_result",
                ["protocol_version"] = ProtocolVersion,
                ["request_id"] = requestId,
                ["status"] = "accepted",
                ["job_id"] = jobId,
            }, cancellationToken);

            _ = RunTestsJobStubAsync(record, mode, filter);
        }

        private async Task HandleGetJobStatusAsync(Dictionary<string, object> message, CancellationToken cancellationToken)
        {
            var requestId = GetString(message, "request_id");
            var jobId = GetString(message, "job_id");

            if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(jobId))
            {
                await SendProtocolErrorAsync(requestId, "ERR_INVALID_PARAMS", "request_id and job_id are required", cancellationToken);
                return;
            }

            JobRecord record;
            lock (_jobGate)
            {
                if (!_jobs.TryGetValue(jobId, out record))
                {
                    record = null;
                }
            }

            if (record == null)
            {
                await SendProtocolErrorAsync(requestId, "ERR_JOB_NOT_FOUND", $"unknown job_id: {jobId}", cancellationToken);
                return;
            }

            Dictionary<string, object> resultPayload;
            string stateWire;
            lock (record.Gate)
            {
                stateWire = ToWireState(record.State);
                resultPayload = record.Result;
            }

            await SendMessageAsync(new Dictionary<string, object>
            {
                ["type"] = "job_status",
                ["protocol_version"] = ProtocolVersion,
                ["request_id"] = requestId,
                ["job_id"] = jobId,
                ["state"] = stateWire,
                ["progress"] = null,
                ["result"] = resultPayload,
            }, cancellationToken);
        }

        private async Task HandleCancelAsync(Dictionary<string, object> message, CancellationToken cancellationToken)
        {
            var requestId = GetString(message, "request_id");
            var targetJobId = GetString(message, "target_job_id");

            if (string.IsNullOrEmpty(requestId))
            {
                await SendProtocolErrorAsync(requestId, "ERR_INVALID_REQUEST", "request_id is required", cancellationToken);
                return;
            }

            if (string.IsNullOrEmpty(targetJobId))
            {
                await SendProtocolErrorAsync(requestId, "ERR_CANCEL_NOT_SUPPORTED", "cancel target is not supported", cancellationToken);
                return;
            }

            JobRecord record;
            lock (_jobGate)
            {
                if (!_jobs.TryGetValue(targetJobId, out record))
                {
                    record = null;
                }
            }

            if (record == null)
            {
                await SendProtocolErrorAsync(requestId, "ERR_JOB_NOT_FOUND", $"unknown job_id: {targetJobId}", cancellationToken);
                return;
            }

            string status;
            lock (record.Gate)
            {
                switch (record.State)
                {
                    case UnityMcpJobState.Queued:
                        record.State = UnityMcpJobState.Cancelled;
                        record.Result = BuildCancelledJobResult();
                        record.CancelTokenSource.Cancel();
                        status = "cancelled";
                        break;
                    case UnityMcpJobState.Running:
                        record.CancelTokenSource.Cancel();
                        status = "cancel_requested";
                        break;
                    default:
                        status = "rejected";
                        break;
                }
            }

            await SendMessageAsync(new Dictionary<string, object>
            {
                ["type"] = "cancel_result",
                ["protocol_version"] = ProtocolVersion,
                ["request_id"] = requestId,
                ["status"] = status,
            }, cancellationToken);
        }

        private Dictionary<string, object> ExecuteSyncTool(string toolName, Dictionary<string, object> parameters)
        {
            if (string.Equals(toolName, "read_console", StringComparison.Ordinal))
            {
                var maxEntries = GetInt(parameters, "max_entries") ?? 200;
                if (maxEntries < 1 || maxEntries > 2000)
                {
                    throw new UnityMcpPluginException("ERR_INVALID_PARAMS", "max_entries must be 1..2000");
                }

                return UnityMcpLogBuffer.Read(maxEntries);
            }

            if (string.Equals(toolName, "get_editor_state", StringComparison.Ordinal))
            {
                lock (_stateGate)
                {
                    var connected = _connectedPort > 0;
                    return new Dictionary<string, object>
                    {
                        ["server_state"] = connected ? "ready" : "waiting_editor",
                        ["editor_state"] = ToWireState(_editorState),
                        ["connected"] = connected,
                        ["last_editor_status_seq"] = _editorSeq,
                    };
                }
            }

            throw new UnityMcpPluginException("ERR_UNKNOWN_COMMAND", $"unsupported tool: {toolName}");
        }

        private async Task RunTestsJobStubAsync(JobRecord record, string mode, string filter)
        {
            try
            {
                lock (record.Gate)
                {
                    if (record.State == UnityMcpJobState.Cancelled)
                    {
                        return;
                    }

                    record.State = UnityMcpJobState.Running;
                }

                await Task.Delay(250, record.CancelTokenSource.Token);

                lock (record.Gate)
                {
                    if (record.CancelTokenSource.IsCancellationRequested)
                    {
                        record.State = UnityMcpJobState.Cancelled;
                        record.Result = BuildCancelledJobResult();
                        return;
                    }

                    // Step1 scope: transport implementation first. Actual test runner integration comes next phase.
                    record.State = UnityMcpJobState.Failed;
                    record.Result = new Dictionary<string, object>
                    {
                        ["summary"] = new Dictionary<string, object>
                        {
                            ["total"] = 0,
                            ["passed"] = 0,
                            ["failed"] = 0,
                            ["skipped"] = 0,
                            ["duration_ms"] = 0,
                        },
                        ["failed_tests"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["name"] = "run_tests",
                                ["message"] = "run_tests execution is not implemented in step1",
                                ["stack_trace"] = string.Empty,
                            },
                        },
                        ["mode"] = mode,
                        ["filter"] = filter,
                    };
                }
            }
            catch (OperationCanceledException)
            {
                lock (record.Gate)
                {
                    record.State = UnityMcpJobState.Cancelled;
                    record.Result = BuildCancelledJobResult();
                }
            }
            catch (Exception ex)
            {
                lock (record.Gate)
                {
                    record.State = UnityMcpJobState.Failed;
                    record.Result = new Dictionary<string, object>
                    {
                        ["summary"] = new Dictionary<string, object>
                        {
                            ["total"] = 0,
                            ["passed"] = 0,
                            ["failed"] = 1,
                            ["skipped"] = 0,
                            ["duration_ms"] = 0,
                        },
                        ["failed_tests"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["name"] = "run_tests",
                                ["message"] = ex.Message,
                                ["stack_trace"] = ex.StackTrace ?? string.Empty,
                            },
                        },
                    };
                }
            }
        }

        private static Dictionary<string, object> BuildCancelledJobResult()
        {
            return new Dictionary<string, object>
            {
                ["summary"] = new Dictionary<string, object>
                {
                    ["total"] = 0,
                    ["passed"] = 0,
                    ["failed"] = 0,
                    ["skipped"] = 0,
                    ["duration_ms"] = 0,
                },
                ["failed_tests"] = new List<object>(),
            };
        }

        private async Task SendHelloAsync(CancellationToken cancellationToken)
        {
            Dictionary<string, object> hello;
            lock (_stateGate)
            {
                hello = new Dictionary<string, object>
                {
                    ["type"] = "hello",
                    ["protocol_version"] = ProtocolVersion,
                    ["plugin_version"] = PluginVersion,
                    ["state"] = ToWireState(_editorState),
                };
            }

            await SendMessageAsync(hello, cancellationToken);
        }

        private async Task SendCurrentEditorStatusAsync(CancellationToken cancellationToken)
        {
            EditorBridgeState state;
            ulong seq;
            lock (_stateGate)
            {
                _editorSeq += 1;
                state = _editorState;
                seq = _editorSeq;
            }

            await SendMessageAsync(new Dictionary<string, object>
            {
                ["type"] = "editor_status",
                ["protocol_version"] = ProtocolVersion,
                ["state"] = ToWireState(state),
                ["seq"] = seq,
            }, cancellationToken);
        }

        private async Task TrySendEditorStatusAsync(EditorBridgeState state, ulong seq)
        {
            if (_connectedPort <= 0)
            {
                return;
            }

            try
            {
                await SendMessageAsync(new Dictionary<string, object>
                {
                    ["type"] = "editor_status",
                    ["protocol_version"] = ProtocolVersion,
                    ["state"] = ToWireState(state),
                    ["seq"] = seq,
                }, _lifecycleCts.Token);
            }
            catch (Exception ex)
            {
                LogWarn("Failed to send editor_status", ("error", ex.Message), ("seq", seq));
            }
        }

        private async Task SendPongAsync(CancellationToken cancellationToken)
        {
            EditorBridgeState state;
            ulong seq;
            lock (_stateGate)
            {
                state = _editorState;
                seq = _editorSeq;
            }

            await SendMessageAsync(new Dictionary<string, object>
            {
                ["type"] = "pong",
                ["protocol_version"] = ProtocolVersion,
                ["editor_state"] = ToWireState(state),
                ["seq"] = seq,
            }, cancellationToken);
        }

        private async Task SendProtocolErrorAsync(string requestId, string code, string message, CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object>
            {
                ["type"] = "error",
                ["protocol_version"] = ProtocolVersion,
                ["error"] = new Dictionary<string, object>
                {
                    ["code"] = code,
                    ["message"] = message,
                },
            };

            if (!string.IsNullOrEmpty(requestId))
            {
                payload["request_id"] = requestId;
            }

            await SendMessageAsync(payload, cancellationToken);
        }

        private async Task SendMessageAsync(Dictionary<string, object> message, CancellationToken cancellationToken)
        {
            var socket = GetCurrentSocket();
            if (socket == null || socket.State != WebSocketState.Open)
            {
                throw new UnityMcpPluginException("ERR_UNITY_DISCONNECTED", "bridge socket is not connected");
            }

            var json = MiniJson.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task<bool> WaitForConnectedPortAsync(int targetPort)
        {
            for (int attempt = 0; attempt < ReconfigureMaxAttempts; attempt += 1)
            {
                if (GetConnectedPort() == targetPort)
                {
                    return true;
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Handler(int port)
                {
                    if (port == targetPort)
                    {
                        tcs.TrySetResult(true);
                    }
                }

                lock (_connectedEventGate)
                {
                    ConnectedPortChanged += Handler;
                }

                try
                {
                    var timeoutTask = Task.Delay(ReconfigureConnectTimeoutMs);
                    var completed = await Task.WhenAny(tcs.Task, timeoutTask);
                    if (completed == tcs.Task)
                    {
                        return true;
                    }
                }
                finally
                {
                    lock (_connectedEventGate)
                    {
                        ConnectedPortChanged -= Handler;
                    }
                }

                RequestReconnect();
                await Task.Delay(ReconfigureRetryIntervalMs);
            }

            return GetConnectedPort() == targetPort;
        }

        private void RaiseConnectedPortChanged(int port)
        {
            Action<int> handlers;
            lock (_connectedEventGate)
            {
                handlers = ConnectedPortChanged;
            }

            handlers?.Invoke(port);
        }

        private void ResetEditorSequenceForNewSession()
        {
            lock (_stateGate)
            {
                _editorSeq = 0;
            }
        }

        private int GetDesiredPort()
        {
            lock (_gate)
            {
                return _desiredPort;
            }
        }

        private int GetConnectedPort()
        {
            lock (_gate)
            {
                return _connectedPort;
            }
        }

        private ClientWebSocket GetCurrentSocket()
        {
            lock (_socketGate)
            {
                return _socket;
            }
        }

        private void CancelCurrentSession()
        {
            CancellationTokenSource current;
            lock (_gate)
            {
                current = _sessionCts;
            }

            if (current == null)
            {
                return;
            }

            try
            {
                current.Cancel();
            }
            catch
            {
                // no-op
            }
        }

        private void RequestReconnect()
        {
            CancelCurrentSession();
            try
            {
                _reconnectSignal.Release();
            }
            catch
            {
                // no-op
            }
        }

        private async Task<bool> WaitReconnectSignalOrDelayAsync(double delayMs, CancellationToken cancellationToken)
        {
            if (delayMs <= 0)
            {
                return true;
            }

            var waitSignalTask = _reconnectSignal.WaitAsync(cancellationToken);
            var delayTask = Task.Delay((int)delayMs, cancellationToken);

            var completed = await Task.WhenAny(waitSignalTask, delayTask);
            if (completed == waitSignalTask)
            {
                await waitSignalTask;
                DrainReconnectSignal();
                return true;
            }

            return false;
        }

        private void DrainReconnectSignal()
        {
            while (_reconnectSignal.CurrentCount > 0)
            {
                _reconnectSignal.Wait(0);
            }
        }

        private double ApplyJitter(double baseMs)
        {
            lock (_random)
            {
                var jitter = (_random.NextDouble() * 2.0 - 1.0) * ReconnectJitterRatio;
                return Math.Max(0, baseMs * (1.0 + jitter));
            }
        }

        private void SafeDisposeSocket()
        {
            ClientWebSocket socket;
            lock (_socketGate)
            {
                socket = _socket;
                _socket = null;
            }

            if (socket == null)
            {
                return;
            }

            SafeCloseSocket(socket, "plugin-shutdown");
            socket.Dispose();
        }

        private void SafeCloseCurrentSocket(string reason)
        {
            var socket = GetCurrentSocket();
            if (socket == null)
            {
                return;
            }

            SafeCloseSocket(socket, reason);
        }

        private static void SafeCloseSocket(ClientWebSocket socket, string reason)
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None).Wait(300);
                }
                catch
                {
                    // no-op
                }
            }
        }

        private static string ToWireState(EditorBridgeState state)
        {
            switch (state)
            {
                case EditorBridgeState.Ready:
                    return "ready";
                case EditorBridgeState.Compiling:
                    return "compiling";
                case EditorBridgeState.Reloading:
                    return "reloading";
                default:
                    return "ready";
            }
        }

        private static string ToWireState(UnityMcpJobState state)
        {
            switch (state)
            {
                case UnityMcpJobState.Queued:
                    return "queued";
                case UnityMcpJobState.Running:
                    return "running";
                case UnityMcpJobState.Succeeded:
                    return "succeeded";
                case UnityMcpJobState.Failed:
                    return "failed";
                case UnityMcpJobState.Timeout:
                    return "timeout";
                case UnityMcpJobState.Cancelled:
                    return "cancelled";
                default:
                    return "failed";
            }
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value as string;
        }

        private static int? GetInt(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                return (int)longValue;
            }

            if (value is double doubleValue)
            {
                return (int)doubleValue;
            }

            if (value is string stringValue && int.TryParse(stringValue, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value as Dictionary<string, object>;
        }

        private static void LogInfo(string message, params (string Key, object Value)[] context)
        {
            LogInternal(LogType.Log, message, context);
        }

        private static void LogWarn(string message, params (string Key, object Value)[] context)
        {
            LogInternal(LogType.Warning, message, context);
        }

        private static void LogError(string message, params (string Key, object Value)[] context)
        {
            LogInternal(LogType.Error, message, context);
        }

        private static void LogInternal(LogType type, string message, params (string Key, object Value)[] context)
        {
            var payload = new Dictionary<string, object>
            {
                ["level"] = type == LogType.Error ? "ERROR" : type == LogType.Warning ? "WARN" : "INFO",
                ["ts"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["msg"] = message,
            };

            for (var i = 0; i < context.Length; i += 1)
            {
                payload[context[i].Key] = context[i].Value;
            }

            var text = MiniJson.Serialize(payload);
            switch (type)
            {
                case LogType.Error:
                    Debug.LogError(text);
                    break;
                case LogType.Warning:
                    Debug.LogWarning(text);
                    break;
                default:
                    Debug.Log(text);
                    break;
            }
        }

        private sealed class JobRecord
        {
            internal JobRecord(string jobId)
            {
                JobId = jobId;
                State = UnityMcpJobState.Queued;
                Result = new Dictionary<string, object>();
                CancelTokenSource = new CancellationTokenSource();
            }

            internal string JobId { get; }
            internal object Gate { get; } = new object();
            internal UnityMcpJobState State { get; set; }
            internal Dictionary<string, object> Result { get; set; }
            internal CancellationTokenSource CancelTokenSource { get; }
        }

        private sealed class UnityMcpPluginException : Exception
        {
            internal UnityMcpPluginException(string code, string message) : base(message)
            {
                Code = code;
            }

            internal string Code { get; }
        }
    }

    internal static class UnityMcpMainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        private static int _mainThreadId;
        private static bool _initialized;

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update += Drain;
        }

        internal static Task<T> InvokeAsync<T>(Func<T> func)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                return Task.FromResult(func());
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Enqueue(() =>
            {
                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        private static void Drain()
        {
            while (Queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    internal static class UnityMcpLogBuffer
    {
        private const int MaxEntries = 5000;

        private static readonly object Gate = new object();
        private static readonly List<Dictionary<string, object>> Entries = new List<Dictionary<string, object>>(MaxEntries);

        private static bool _initialized;

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
        }

        internal static Dictionary<string, object> Read(int maxEntries)
        {
            lock (Gate)
            {
                var total = Entries.Count;
                var take = Math.Min(maxEntries, total);
                var start = total - take;

                var sliced = new List<object>(take);
                for (var i = start; i < total; i += 1)
                {
                    sliced.Add(new Dictionary<string, object>(Entries[i]));
                }

                return new Dictionary<string, object>
                {
                    ["entries"] = sliced,
                    ["count"] = take,
                    ["truncated"] = total > take,
                };
            }
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            lock (Gate)
            {
                Entries.Add(new Dictionary<string, object>
                {
                    ["type"] = ToWireLogType(type),
                    ["message"] = condition ?? string.Empty,
                    ["stack_trace"] = stackTrace ?? string.Empty,
                });

                if (Entries.Count > MaxEntries)
                {
                    Entries.RemoveRange(0, Entries.Count - MaxEntries);
                }
            }
        }

        private static string ToWireLogType(LogType type)
        {
            switch (type)
            {
                case LogType.Warning:
                    return "warning";
                case LogType.Error:
                    return "error";
                case LogType.Assert:
                    return "assert";
                case LogType.Exception:
                    return "exception";
                default:
                    return "log";
            }
        }
    }

    internal static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (json == null)
            {
                return null;
            }

            return Parser.Parse(json);
        }

        public static string Serialize(object obj)
        {
            return Serializer.Serialize(obj);
        }

        private sealed class Parser : IDisposable
        {
            private readonly StringReader _reader;

            private Parser(string jsonString)
            {
                _reader = new StringReader(jsonString);
            }

            public static object Parse(string jsonString)
            {
                using (var instance = new Parser(jsonString))
                {
                    return instance.ParseValue();
                }
            }

            public void Dispose()
            {
                _reader.Dispose();
            }

            private enum Token
            {
                None,
                CurlyOpen,
                CurlyClose,
                SquaredOpen,
                SquaredClose,
                Colon,
                Comma,
                String,
                Number,
                True,
                False,
                Null,
            }

            private Dictionary<string, object> ParseObject()
            {
                var table = new Dictionary<string, object>(StringComparer.Ordinal);

                _reader.Read();

                while (true)
                {
                    var token = NextToken;
                    if (token == Token.None)
                    {
                        return null;
                    }

                    if (token == Token.Comma)
                    {
                        continue;
                    }

                    if (token == Token.CurlyClose)
                    {
                        return table;
                    }

                    var name = ParseString();
                    if (name == null)
                    {
                        return null;
                    }

                    if (NextToken != Token.Colon)
                    {
                        return null;
                    }

                    _reader.Read();

                    table[name] = ParseValue();
                }
            }

            private List<object> ParseArray()
            {
                var array = new List<object>();

                _reader.Read();

                var parsing = true;
                while (parsing)
                {
                    var token = NextToken;
                    switch (token)
                    {
                        case Token.None:
                            return null;
                        case Token.Comma:
                            continue;
                        case Token.SquaredClose:
                            parsing = false;
                            break;
                        default:
                            array.Add(ParseValue());
                            break;
                    }
                }

                return array;
            }

            private object ParseValue()
            {
                switch (NextToken)
                {
                    case Token.String:
                        return ParseString();
                    case Token.Number:
                        return ParseNumber();
                    case Token.CurlyOpen:
                        return ParseObject();
                    case Token.SquaredOpen:
                        return ParseArray();
                    case Token.True:
                        return true;
                    case Token.False:
                        return false;
                    case Token.Null:
                        return null;
                    default:
                        return null;
                }
            }

            private string ParseString()
            {
                var s = new StringBuilder();
                char c;

                _reader.Read();

                var parsing = true;
                while (parsing)
                {
                    if (_reader.Peek() == -1)
                    {
                        break;
                    }

                    c = NextChar;
                    switch (c)
                    {
                        case '"':
                            parsing = false;
                            break;
                        case '\\':
                            if (_reader.Peek() == -1)
                            {
                                parsing = false;
                                break;
                            }

                            c = NextChar;
                            switch (c)
                            {
                                case '"':
                                case '\\':
                                case '/':
                                    s.Append(c);
                                    break;
                                case 'b':
                                    s.Append('\b');
                                    break;
                                case 'f':
                                    s.Append('\f');
                                    break;
                                case 'n':
                                    s.Append('\n');
                                    break;
                                case 'r':
                                    s.Append('\r');
                                    break;
                                case 't':
                                    s.Append('\t');
                                    break;
                                case 'u':
                                {
                                    var hex = new char[4];
                                    for (var i = 0; i < 4; i++)
                                    {
                                        hex[i] = NextChar;
                                    }

                                    s.Append((char)Convert.ToInt32(new string(hex), 16));
                                    break;
                                }
                            }

                            break;
                        default:
                            s.Append(c);
                            break;
                    }
                }

                return s.ToString();
            }

            private object ParseNumber()
            {
                var number = NextWord;
                if (number.IndexOf('.') == -1)
                {
                    if (long.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedInt))
                    {
                        return parsedInt;
                    }
                }

                if (double.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDouble))
                {
                    return parsedDouble;
                }

                return 0;
            }

            private void EatWhitespace()
            {
                while (_reader.Peek() != -1 && char.IsWhiteSpace(PeekChar))
                {
                    _reader.Read();
                }
            }

            private char PeekChar
            {
                get { return Convert.ToChar(_reader.Peek()); }
            }

            private char NextChar
            {
                get { return Convert.ToChar(_reader.Read()); }
            }

            private string NextWord
            {
                get
                {
                    var word = new StringBuilder();

                    while (!IsWordBreak(PeekChar))
                    {
                        word.Append(NextChar);
                        if (_reader.Peek() == -1)
                        {
                            break;
                        }
                    }

                    return word.ToString();
                }
            }

            private Token NextToken
            {
                get
                {
                    EatWhitespace();

                    if (_reader.Peek() == -1)
                    {
                        return Token.None;
                    }

                    switch (PeekChar)
                    {
                        case '{':
                            return Token.CurlyOpen;
                        case '}':
                            _reader.Read();
                            return Token.CurlyClose;
                        case '[':
                            return Token.SquaredOpen;
                        case ']':
                            _reader.Read();
                            return Token.SquaredClose;
                        case ',':
                            _reader.Read();
                            return Token.Comma;
                        case '"':
                            return Token.String;
                        case ':':
                            return Token.Colon;
                        case '0':
                        case '1':
                        case '2':
                        case '3':
                        case '4':
                        case '5':
                        case '6':
                        case '7':
                        case '8':
                        case '9':
                        case '-':
                            return Token.Number;
                    }

                    switch (NextWord)
                    {
                        case "false":
                            return Token.False;
                        case "true":
                            return Token.True;
                        case "null":
                            return Token.Null;
                    }

                    return Token.None;
                }
            }

            private static bool IsWordBreak(char c)
            {
                return char.IsWhiteSpace(c) || c == ',' || c == ':' || c == ']' || c == '}' || c == '[' || c == '{' || c == '"';
            }
        }

        private sealed class Serializer
        {
            private readonly StringBuilder _builder;

            private Serializer()
            {
                _builder = new StringBuilder();
            }

            public static string Serialize(object obj)
            {
                var instance = new Serializer();
                instance.SerializeValue(obj);
                return instance._builder.ToString();
            }

            private void SerializeValue(object value)
            {
                if (value == null)
                {
                    _builder.Append("null");
                    return;
                }

                if (value is string stringValue)
                {
                    SerializeString(stringValue);
                    return;
                }

                if (value is bool boolValue)
                {
                    _builder.Append(boolValue ? "true" : "false");
                    return;
                }

                if (value is IDictionary dictionary)
                {
                    SerializeObject(dictionary);
                    return;
                }

                if (value is IList list)
                {
                    SerializeArray(list);
                    return;
                }

                if (value is char charValue)
                {
                    SerializeString(new string(charValue, 1));
                    return;
                }

                SerializeOther(value);
            }

            private void SerializeObject(IDictionary obj)
            {
                var first = true;
                _builder.Append('{');

                foreach (var e in obj.Keys)
                {
                    if (!first)
                    {
                        _builder.Append(',');
                    }

                    SerializeString(e.ToString());
                    _builder.Append(':');
                    SerializeValue(obj[e]);
                    first = false;
                }

                _builder.Append('}');
            }

            private void SerializeArray(IList array)
            {
                _builder.Append('[');

                var first = true;
                for (var i = 0; i < array.Count; i++)
                {
                    if (!first)
                    {
                        _builder.Append(',');
                    }

                    SerializeValue(array[i]);
                    first = false;
                }

                _builder.Append(']');
            }

            private void SerializeString(string str)
            {
                _builder.Append('"');

                var charArray = str.ToCharArray();
                for (var i = 0; i < charArray.Length; i++)
                {
                    var c = charArray[i];
                    switch (c)
                    {
                        case '"':
                            _builder.Append("\\\"");
                            break;
                        case '\\':
                            _builder.Append("\\\\");
                            break;
                        case '\b':
                            _builder.Append("\\b");
                            break;
                        case '\f':
                            _builder.Append("\\f");
                            break;
                        case '\n':
                            _builder.Append("\\n");
                            break;
                        case '\r':
                            _builder.Append("\\r");
                            break;
                        case '\t':
                            _builder.Append("\\t");
                            break;
                        default:
                            if (c < 32 || c > 126)
                            {
                                _builder.Append("\\u");
                                _builder.Append(((int)c).ToString("x4"));
                            }
                            else
                            {
                                _builder.Append(c);
                            }

                            break;
                    }
                }

                _builder.Append('"');
            }

            private void SerializeOther(object value)
            {
                if (value is float ||
                    value is int ||
                    value is uint ||
                    value is long ||
                    value is double ||
                    value is sbyte ||
                    value is byte ||
                    value is short ||
                    value is ushort ||
                    value is ulong ||
                    value is decimal)
                {
                    _builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                }
                else
                {
                    SerializeString(value.ToString());
                }
            }
        }
    }
}
