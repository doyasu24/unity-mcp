using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace UnityMcpPlugin
{
    internal sealed class PluginRuntime
    {
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
        private const int ConnectivityWarningThrottleMs = 30000;

        private readonly object _gate = new();
        private readonly SemaphoreSlim _reconfigureLock = new(1, 1);

        private readonly EditorStateTracker _editorStateTracker = new();
        private readonly CommandExecutor _commandExecutor;
        private readonly JobExecutor _jobExecutor;
        private readonly CommandRouter _commandRouter;
        private readonly BridgeConnectionManager _connectionManager;

        private int _desiredPort;
        private int _activePort;
        private int _consecutiveConnectFailures;
        private bool _connectivityIssueAnnounced;
        private DateTimeOffset _lastConnectivityWarningUtc;

        private bool _initialized;
        private bool _shuttingDown;

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
                _lastConnectivityWarningUtc = DateTimeOffset.MinValue;
                _shuttingDown = false;

                LogBuffer.Initialize();
                MainThreadDispatcher.Initialize();
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
            LogBuffer.Shutdown();
            MainThreadDispatcher.Shutdown();

            lock (_gate)
            {
                _initialized = false;
                _consecutiveConnectFailures = 0;
                _connectivityIssueAnnounced = false;
                _lastConnectivityWarningUtc = DateTimeOffset.MinValue;
                _shuttingDown = false;
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
                }

                _connectionManager.RequestReconnect();
                var connectedNewPort = await _connectionManager.WaitForConnectedPortAsync(
                    newPort,
                    ReconfigureMaxAttempts,
                    ReconfigureConnectTimeoutMs,
                    ReconfigureRetryIntervalMs,
                    CancellationToken.None);
                if (connectedNewPort)
                {
                    lock (_gate)
                    {
                        _activePort = newPort;
                    }

                    PluginLogger.UserInfo("Port reconfigure applied", ("old_port", oldPort), ("new_port", newPort));
                    return PortReconfigureResult.Applied(newPort);
                }

                lock (_gate)
                {
                    _desiredPort = oldPort;
                }

                _connectionManager.RequestReconnect();
                var rollbackSuccess = await _connectionManager.WaitForConnectedPortAsync(
                    oldPort,
                    ReconfigureMaxAttempts,
                    ReconfigureConnectTimeoutMs,
                    ReconfigureRetryIntervalMs,
                    CancellationToken.None);
                if (rollbackSuccess)
                {
                    lock (_gate)
                    {
                        _activePort = oldPort;
                    }

                    PluginLogger.UserWarn("Port reconfigure rolled back", ("old_port", oldPort), ("new_port", newPort));
                    return PortReconfigureResult.RolledBack(
                        oldPort,
                        "ERR_RECONFIG_CONNECT_TIMEOUT",
                        "failed to connect to new port and rolled back to previous port");
                }

                PluginLogger.UserError("Port reconfigure rollback failed", ("old_port", oldPort), ("new_port", newPort));
                return PortReconfigureResult.Failed(
                    GetActivePort(),
                    "ERR_RECONFIG_ROLLBACK_FAILED",
                    "failed to connect to new port and rollback port");
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

        private async Task OnConnectedAsync(int port, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _activePort = port;
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
            ResetConnectivityIssueTracking();
            PluginLogger.UserInfo("Connected to server", ("port", port));

            PluginLogger.DevInfo("Bridge connected", ("port", port), ("uri", $"ws://{Host}:{port}{UnityWsPath}"));
        }

        private async Task OnIncomingTextMessageAsync(int port, string raw, CancellationToken cancellationToken)
        {
            await HandleIncomingMessageAsync(raw, cancellationToken);
        }

        private void OnDisconnected(int port)
        {
            lock (_gate)
            {
                if (_shuttingDown)
                {
                    return;
                }
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

        private async Task HandleIncomingMessageAsync(string raw, CancellationToken cancellationToken)
        {
            if (!Payload.TryParseDocument(raw, out var document))
            {
                PluginLogger.DevWarn("Received invalid JSON message");
                return;
            }

            var message = document;

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

        private EditorSnapshot GetEditorSnapshot()
        {
            var connected = _connectionManager.GetConnectedPort() > 0;
            return _editorStateTracker.Snapshot(connected);
        }

        private void ResetConnectivityIssueTracking()
        {
            lock (_gate)
            {
                _connectivityIssueAnnounced = false;
                _consecutiveConnectFailures = 0;
            }
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
    }
}
