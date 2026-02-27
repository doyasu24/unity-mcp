using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityMcpPlugin
{
    internal sealed class PluginRuntime
    {
        private const string Host = "127.0.0.1";
        private const string UnityWsPath = "/unity";
        private const int MaxMessageBytes = 1024 * 1024;

        private const int ReconnectInitialMs = 200;
        private const double ReconnectMultiplier = 1.8;
        private const int ReconnectMaxBackoffMs = 5000;
        private const double ReconnectJitterRatio = 0.2;

        private const int ReconfigureConnectTimeoutMs = 10000;
        private const int ConnectivityWarningThrottleMs = 30000;
        private const int MultiEditorConflictReconnectDelayMs = 10000;
        private const int MultiEditorConflictWarningThrottleMs = 30000;

        private readonly object _gate = new();
        private readonly SemaphoreSlim _reconfigureLock = new(1, 1);

        private readonly EditorStateTracker _editorStateTracker = new();
        private readonly CommandExecutor _commandExecutor;
        private readonly JobExecutor _jobExecutor;
        private readonly CommandRouter _commandRouter;
        private readonly BridgeConnectionManager _connectionManager;
        private readonly string _editorInstanceId;
        private readonly string _pluginSessionId;
        private readonly ConcurrentQueue<QueuedInboundMessage> _inboundMessages = new();
        private readonly SemaphoreSlim _inboundMessageSignal = new(0, int.MaxValue);

        private int _desiredPort;
        private int _activePort;
        private int _consecutiveConnectFailures;
        private bool _connectivityIssueAnnounced;
        private bool _multiEditorConflictAnnounced;
        private bool _multiEditorConflictActive;
        private DateTimeOffset _lastConnectivityWarningUtc;
        private DateTimeOffset _lastMultiEditorConflictWarningUtc;

        private bool _initialized;
        private bool _shuttingDown;
        private ulong _currentConnectAttemptSeq;
        private CancellationTokenSource _inboundProcessorCts;
        private Task _inboundProcessorTask;

        private readonly struct QueuedInboundMessage
        {
            internal QueuedInboundMessage(JObject message)
            {
                Message = message;
            }

            internal JObject Message { get; }
        }

        private PluginRuntime()
        {
            _jobExecutor = new();
            _commandExecutor = new(GetEditorSnapshot);
            _commandRouter = new(_commandExecutor, _jobExecutor, SendMessageAsync, SendProtocolErrorAsync);
            _connectionManager = new BridgeConnectionManager(
                Host,
                UnityWsPath,
                MaxMessageBytes,
                ReconfigureConnectTimeoutMs,
                ReconnectInitialMs,
                ReconnectMultiplier,
                ReconnectMaxBackoffMs,
                ReconnectJitterRatio,
                GetDesiredPort,
                OnConnectedAsync,
                OnIncomingTextMessageAsync,
                OnDisconnected,
                OnSessionError);

            _editorInstanceId = BuildEditorInstanceId();
            _pluginSessionId = Guid.NewGuid().ToString("D");
            _inboundProcessorCts = new CancellationTokenSource();
            _inboundProcessorTask = Task.CompletedTask;
        }

        internal static PluginRuntime Instance { get; } = new();

        internal void Initialize()
        {
            lock (_gate)
            {
                if (_initialized)
                {
                    return;
                }

                var settings = PluginSettings.instance;
                var validation = settings.Validate();
                if (!validation.IsValid)
                {
                    PluginLogger.Error(
                        "Plugin initialization aborted by invalid settings",
                        ("code", validation.Code),
                        ("message", validation.Message));
                    return;
                }

                _desiredPort = settings.port;
                _activePort = settings.port;
                _consecutiveConnectFailures = 0;
                _connectivityIssueAnnounced = false;
                _multiEditorConflictAnnounced = false;
                _multiEditorConflictActive = false;
                _lastConnectivityWarningUtc = DateTimeOffset.MinValue;
                _lastMultiEditorConflictWarningUtc = DateTimeOffset.MinValue;
                _shuttingDown = false;
                _currentConnectAttemptSeq = 0;

                LogBuffer.Initialize();
                MainThreadDispatcher.Initialize();
                _inboundProcessorCts = new CancellationTokenSource();
                _inboundProcessorTask = Task.Run(() => InboundMessageLoopAsync(_inboundProcessorCts.Token));
                _connectionManager.Start();

                _initialized = true;

                PluginLogger.UserInfo("Unity MCP plugin initialized", ("port", _desiredPort));
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

            _connectionManager.Stop(TimeSpan.FromSeconds(2));
            StopInboundMessageProcessor();
            LogBuffer.Shutdown();
            MainThreadDispatcher.Shutdown();

            lock (_gate)
            {
                _initialized = false;
                _consecutiveConnectFailures = 0;
                _connectivityIssueAnnounced = false;
                _multiEditorConflictAnnounced = false;
                _multiEditorConflictActive = false;
                _lastConnectivityWarningUtc = DateTimeOffset.MinValue;
                _lastMultiEditorConflictWarningUtc = DateTimeOffset.MinValue;
                _shuttingDown = false;
                _currentConnectAttemptSeq = 0;
            }

            PluginLogger.UserInfo("Unity MCP plugin stopped");
        }

        internal int GetActivePort()
        {
            lock (_gate)
            {
                if (!_initialized)
                {
                    return PluginSettings.instance.port;
                }

                return _activePort;
            }
        }

        internal string GetRuntimeSummary()
        {
            int desiredPort;
            int connectedPort;

            lock (_gate)
            {
                if (!_initialized)
                {
                    return "stopped";
                }

                desiredPort = _desiredPort;
                connectedPort = _connectionManager.GetConnectedPort();
            }

            var snapshot = _editorStateTracker.Snapshot(connectedPort > 0);
            var connection = snapshot.Connected ? "connected" : "waiting_editor";
            return $"{connection}, desired_port={desiredPort}, connected_port={connectedPort}, editor_state={Wire.ToWireState(snapshot.State)}, seq={snapshot.Seq}";
        }

        internal void PublishEditorState(EditorBridgeState nextState)
        {
            var change = _editorStateTracker.Publish(nextState);
            if (!change.Changed)
            {
                return;
            }

            PluginLogger.DevInfo("Editor state updated", ("state", Wire.ToWireState(change.State)), ("seq", change.Seq));
            _ = TrySendEditorStatusAsync(change.State, change.Seq);
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
                    _activePort = newPort;
                }

                _connectionManager.ClearReconnectDelay();
                _connectionManager.RequestReconnect();
                PluginLogger.UserInfo(
                    "Port setting applied. Reconnecting in background.",
                    ("old_port", oldPort),
                    ("new_port", newPort));
                return PortReconfigureResult.Applied(newPort);
            }
            catch (Exception ex)
            {
                return PortReconfigureResult.Failed(GetActivePort(), "ERR_RECONFIG_FAILED", ex.Message);
            }
            finally
            {
                _reconfigureLock.Release();
            }
        }

        private async Task OnConnectedAsync(int port, ulong connectAttemptSeq, CancellationToken cancellationToken)
        {
            var conflictActive = false;
            lock (_gate)
            {
                _activePort = port;
                conflictActive = _multiEditorConflictActive;
                _currentConnectAttemptSeq = connectAttemptSeq;
            }

            _editorStateTracker.ResetSequence();
            await SendHelloAsync(cancellationToken);

            try
            {
                await SendCurrentEditorStatusAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                PluginLogger.DevWarn("Initial editor_status send failed", ("port", port), ("error", ex.Message));
            }

            if (!conflictActive)
            {
                PluginLogger.DevInfo("Bridge transport connected", ("port", port), ("uri", $"ws://{Host}:{port}{UnityWsPath}"));
            }
        }

        private async Task OnIncomingTextMessageAsync(int port, string raw, CancellationToken cancellationToken)
        {
            if (!Payload.TryParseDocument(raw, out var message))
            {
                PluginLogger.DevWarn("Received invalid JSON message");
                return;
            }

            var protocolVersion = Payload.GetInt(message, "protocol_version");
            var requestId = Payload.GetString(message, "request_id");
            if (protocolVersion != Wire.ProtocolVersion)
            {
                await SendProtocolErrorAsync(requestId, "ERR_INVALID_REQUEST", "protocol_version mismatch", cancellationToken);
                _connectionManager.CloseCurrentSocket("protocol-version-mismatch");
                return;
            }

            var type = Payload.GetString(message, "type");
            if (string.IsNullOrEmpty(type))
            {
                await SendProtocolErrorAsync(requestId, "ERR_INVALID_REQUEST", "type is required", cancellationToken);
                return;
            }

            if (string.Equals(type, "ping", StringComparison.Ordinal))
            {
                await SendPongAsync(cancellationToken);
                return;
            }

            EnqueueInboundMessage(message);
        }

        private void OnDisconnected(int port)
        {
            var conflictActive = false;
            lock (_gate)
            {
                if (_shuttingDown)
                {
                    return;
                }

                conflictActive = _multiEditorConflictActive;
            }

            if (conflictActive)
            {
                return;
            }

            PluginLogger.DevWarn("Bridge disconnected", ("port", port));
        }

        private void OnSessionError(int port, Exception ex)
        {
            var offline = IsServerUnavailableError(ex);
            var attempt = 0;
            var shouldWarnUser = false;

            lock (_gate)
            {
                if (_shuttingDown)
                {
                    return;
                }

                if (offline)
                {
                    _multiEditorConflictActive = false;
                    _consecutiveConnectFailures += 1;
                    attempt = _consecutiveConnectFailures;
                    shouldWarnUser = ShouldEmitConnectivityWarningLocked();
                    if (shouldWarnUser)
                    {
                        _connectivityIssueAnnounced = true;
                    }
                }
            }

            if (offline)
            {
                if (shouldWarnUser)
                {
                    PluginLogger.UserWarn("Disconnected from server. Retrying in background.", ("port", port));
                }

                PluginLogger.DevWarn("Bridge session ended with error", ("port", port), ("attempt", attempt), ("error", ex.Message));
                return;
            }

            PluginLogger.DevWarn("Bridge session ended with error", ("port", port), ("error", ex.Message));
        }

        private async Task InboundMessageLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await _inboundMessageSignal.WaitAsync(cancellationToken);

                    while (_inboundMessages.TryDequeue(out var queued))
                    {
                        await HandleQueuedIncomingMessageAsync(queued.Message, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            catch (Exception ex)
            {
                PluginLogger.DevWarn("Inbound message loop failed", ("error", ex.Message));
            }
        }

        private async Task HandleQueuedIncomingMessageAsync(JObject message, CancellationToken cancellationToken)
        {
            var type = Payload.GetString(message, "type");
            if (string.IsNullOrEmpty(type))
            {
                return;
            }

            if (string.Equals(type, "hello", StringComparison.Ordinal))
            {
                OnServerHelloReceived(message);
            }

            if (string.Equals(type, "error", StringComparison.Ordinal) && TryHandleServerErrorMessage(message))
            {
                return;
            }

            await _commandRouter.RouteAsync(type, message, cancellationToken);
        }

        private async Task SendHelloAsync(CancellationToken cancellationToken)
        {
            var connected = _connectionManager.GetConnectedPort() > 0;
            var snapshot = _editorStateTracker.Snapshot(connected);

            var hello = new
            {
                type = "hello",
                protocol_version = Wire.ProtocolVersion,
                plugin_version = Wire.PluginVersion,
                editor_instance_id = _editorInstanceId,
                plugin_session_id = _pluginSessionId,
                connect_attempt_seq = _currentConnectAttemptSeq,
                state = Wire.ToWireState(snapshot.State),
            };

            await SendMessageAsync(hello, cancellationToken);
        }

        private async Task SendCurrentEditorStatusAsync(CancellationToken cancellationToken)
        {
            var connected = _connectionManager.GetConnectedPort() > 0;
            var snapshot = _editorStateTracker.IncrementSequenceForStatus(connected);

            await SendMessageAsync(new
            {
                type = "editor_status",
                protocol_version = Wire.ProtocolVersion,
                state = Wire.ToWireState(snapshot.State),
                seq = snapshot.Seq,
            }, cancellationToken);
        }

        private async Task TrySendEditorStatusAsync(EditorBridgeState state, ulong seq)
        {
            if (_connectionManager.GetConnectedPort() <= 0)
            {
                return;
            }

            try
            {
                await SendMessageAsync(new
                {
                    type = "editor_status",
                    protocol_version = Wire.ProtocolVersion,
                    state = Wire.ToWireState(state),
                    seq,
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                PluginLogger.DevWarn("Failed to send editor_status", ("error", ex.Message), ("seq", seq));
            }
        }

        private async Task SendPongAsync(CancellationToken cancellationToken)
        {
            var snapshot = GetEditorSnapshot();
            await SendMessageAsync(new
            {
                type = "pong",
                protocol_version = Wire.ProtocolVersion,
                editor_state = Wire.ToWireState(snapshot.State),
                seq = snapshot.Seq,
            }, cancellationToken);
        }

        private async Task SendProtocolErrorAsync(string requestId, string code, string message, CancellationToken cancellationToken)
        {
            var payload = new
            {
                type = "error",
                protocol_version = Wire.ProtocolVersion,
                request_id = requestId,
                error = new
                {
                    code,
                    message,
                },
            };

            await SendMessageAsync(payload, cancellationToken);
        }

        private Task SendMessageAsync(object message, CancellationToken cancellationToken)
        {
            return _connectionManager.SendAsync(message, cancellationToken);
        }

        private int GetDesiredPort()
        {
            lock (_gate)
            {
                return _desiredPort;
            }
        }

        private static string BuildEditorInstanceId()
        {
            var processId = -1;
            try
            {
                processId = Process.GetCurrentProcess().Id;
            }
            catch
            {
                // no-op
            }

            var projectPath = Application.dataPath ?? string.Empty;
            return $"{processId}:{projectPath}";
        }

        private EditorSnapshot GetEditorSnapshot()
        {
            var connected = _connectionManager.GetConnectedPort() > 0;
            return _editorStateTracker.Snapshot(connected);
        }

        private void OnServerHelloReceived(JObject message)
        {
            var connectedPort = _connectionManager.GetConnectedPort();
            var connectionId = Payload.GetString(message, "connection_id");
            var heartbeatIntervalMs = Payload.GetInt(message, "heartbeat_interval_ms");
            var heartbeatTimeoutMs = Payload.GetInt(message, "heartbeat_timeout_ms");
            var heartbeatMissThreshold = Payload.GetInt(message, "heartbeat_miss_threshold");
            lock (_gate)
            {
                _multiEditorConflictAnnounced = false;
                _multiEditorConflictActive = false;
                _lastMultiEditorConflictWarningUtc = DateTimeOffset.MinValue;
            }

            _connectionManager.ClearReconnectDelay();
            ResetConnectivityIssueTracking();
            PluginLogger.UserInfo("Connected to server", ("port", connectedPort), ("connection_id", connectionId));
            PluginLogger.DevInfo(
                "Server heartbeat policy updated",
                ("connection_id", connectionId),
                ("heartbeat_interval_ms", heartbeatIntervalMs),
                ("heartbeat_timeout_ms", heartbeatTimeoutMs),
                ("heartbeat_miss_threshold", heartbeatMissThreshold));
        }

        private void EnqueueInboundMessage(JObject message)
        {
            _inboundMessages.Enqueue(new QueuedInboundMessage(message));
            try
            {
                _inboundMessageSignal.Release();
            }
            catch
            {
                // no-op
            }
        }

        private void StopInboundMessageProcessor()
        {
            try
            {
                _inboundProcessorCts.Cancel();
            }
            catch
            {
                // no-op
            }

            try
            {
                _inboundProcessorTask.Wait(500);
            }
            catch
            {
                // no-op
            }

            while (_inboundMessages.TryDequeue(out _))
            {
                // drop pending inbound messages during shutdown
            }

            _inboundProcessorCts.Dispose();
            _inboundProcessorTask = Task.CompletedTask;
            _inboundProcessorCts = new CancellationTokenSource();
        }

        private bool TryHandleServerErrorMessage(JObject message)
        {
            var error = Payload.GetObjectOrEmpty(message, "error");
            var code = Payload.GetString(error, "code") ?? string.Empty;
            var errorMessage = Payload.GetString(error, "message") ?? string.Empty;

            if (!IsMultiEditorConflictError(code, errorMessage))
            {
                return false;
            }

            var shouldLogUser = false;
            var shouldLogDev = false;
            var connectedPort = _connectionManager.GetConnectedPort();
            lock (_gate)
            {
                if (!_multiEditorConflictAnnounced)
                {
                    _multiEditorConflictAnnounced = true;
                    shouldLogUser = true;
                }

                shouldLogDev = !_multiEditorConflictActive || ShouldEmitMultiEditorConflictWarningLocked();
                _multiEditorConflictActive = true;
            }

            if (shouldLogUser)
            {
                PluginLogger.UserError(
                    "Connection rejected: multiple Unity Editors are trying to use the same MCP server. Close one Editor, or see README > Using Multiple Unity Editors.",
                    ("port", connectedPort));
            }

            if (shouldLogDev)
            {
                PluginLogger.DevWarn(
                    "Server rejected Unity session because another editor is active",
                    ("port", connectedPort),
                    ("code", code),
                    ("message", errorMessage));
            }

            _connectionManager.SetReconnectDelay(TimeSpan.FromMilliseconds(MultiEditorConflictReconnectDelayMs));
            _connectionManager.CloseCurrentSocket("session-already-active");
            return true;
        }

        private void ResetConnectivityIssueTracking()
        {
            lock (_gate)
            {
                _connectivityIssueAnnounced = false;
                _consecutiveConnectFailures = 0;
            }
        }

        private static bool IsMultiEditorConflictError(string code, string message)
        {
            if (!string.Equals(code, "ERR_INVALID_REQUEST", StringComparison.Ordinal))
            {
                return false;
            }

            return message.IndexOf("another Unity websocket session is already active", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsServerUnavailableError(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is WebSocketException or OperationCanceledException or TimeoutException)
                {
                    return true;
                }

                var message = current.Message ?? string.Empty;
                if (message.IndexOf("Unable to connect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("actively refused", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("connection refused", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("No connection could be made", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private bool ShouldEmitConnectivityWarningLocked()
        {
            if (_connectivityIssueAnnounced)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            if ((now - _lastConnectivityWarningUtc).TotalMilliseconds < ConnectivityWarningThrottleMs)
            {
                return false;
            }

            _lastConnectivityWarningUtc = now;
            return true;
        }

        private bool ShouldEmitMultiEditorConflictWarningLocked()
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastMultiEditorConflictWarningUtc).TotalMilliseconds < MultiEditorConflictWarningThrottleMs)
            {
                return false;
            }

            _lastMultiEditorConflictWarningUtc = now;
            return true;
        }
    }
}
