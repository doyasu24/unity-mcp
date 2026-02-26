using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityMcpPlugin
{
    internal sealed class BridgeConnectionManager
    {
        private readonly object _gate = new();
        private readonly object _connectedEventGate = new();
        private readonly object _socketGate = new();

        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SemaphoreSlim _reconnectSignal = new(0, int.MaxValue);

        private readonly Random _random = new();

        private readonly string _host;
        private readonly string _unityWsPath;
        private readonly int _maxMessageBytes;
        private readonly int _connectTimeoutMs;
        private readonly int _reconnectInitialMs;
        private readonly double _reconnectMultiplier;
        private readonly int _reconnectMaxBackoffMs;
        private readonly double _reconnectJitterRatio;

        private readonly Func<int> _desiredPortProvider;
        private readonly Func<int, CancellationToken, Task> _onConnectedAsync;
        private readonly Func<int, string, CancellationToken, Task> _onTextMessageAsync;
        private readonly Action<int> _onDisconnected;
        private readonly Action<int, Exception> _onSessionError;

        private CancellationTokenSource _lifecycleCts;
        private CancellationTokenSource _sessionCts;
        private Task _lifecycleTask;

        private int _connectedPort = -1;
        private bool _started;

        private ClientWebSocket _socket;

        private event Action<int> ConnectedPortChanged;

        internal BridgeConnectionManager(
            string host,
            string unityWsPath,
            int maxMessageBytes,
            int connectTimeoutMs,
            int reconnectInitialMs,
            double reconnectMultiplier,
            int reconnectMaxBackoffMs,
            double reconnectJitterRatio,
            Func<int> desiredPortProvider,
            Func<int, CancellationToken, Task> onConnectedAsync,
            Func<int, string, CancellationToken, Task> onTextMessageAsync,
            Action<int> onDisconnected,
            Action<int, Exception> onSessionError)
        {
            _host = host;
            _unityWsPath = unityWsPath;
            _maxMessageBytes = maxMessageBytes;
            _connectTimeoutMs = connectTimeoutMs;
            _reconnectInitialMs = reconnectInitialMs;
            _reconnectMultiplier = reconnectMultiplier;
            _reconnectMaxBackoffMs = reconnectMaxBackoffMs;
            _reconnectJitterRatio = reconnectJitterRatio;

            _desiredPortProvider = desiredPortProvider;
            _onConnectedAsync = onConnectedAsync;
            _onTextMessageAsync = onTextMessageAsync;
            _onDisconnected = onDisconnected;
            _onSessionError = onSessionError;

            _lifecycleTask = Task.CompletedTask;
            _lifecycleCts = new();
        }

        internal int GetConnectedPort()
        {
            lock (_gate)
            {
                return _connectedPort;
            }
        }

        internal void Start()
        {
            lock (_gate)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
                _lifecycleCts = new();
                _lifecycleTask = Task.Run(() => LifecycleLoopAsync(_lifecycleCts.Token));
            }
        }

        internal void Stop(TimeSpan waitTimeout)
        {
            CancellationTokenSource lifecycle;
            Task lifecycleTask;

            lock (_gate)
            {
                if (!_started)
                {
                    return;
                }

                _started = false;
                lifecycle = _lifecycleCts;
                lifecycleTask = _lifecycleTask;
            }

            try
            {
                lifecycle.Cancel();
            }
            catch
            {
                // no-op
            }

            RequestReconnect();
            CancelCurrentSession();

            try
            {
                Task.WaitAny(new[] { lifecycleTask }, waitTimeout);
            }
            catch
            {
                // no-op
            }

            SafeDisposeSocket();
        }

        internal void RequestReconnect()
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

        internal async Task<bool> WaitForConnectedPortAsync(
            int targetPort,
            int maxAttempts,
            int connectTimeoutMs,
            int retryIntervalMs,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < maxAttempts; attempt += 1)
            {
                if (GetConnectedPort() == targetPort)
                {
                    return true;
                }

                TaskCompletionSource<bool> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                void Handler(int port)
                {
                    if (port == targetPort)
                    {
                        connected.TrySetResult(true);
                    }
                }

                lock (_connectedEventGate)
                {
                    ConnectedPortChanged += Handler;
                }

                try
                {
                    var timeout = Task.Delay(connectTimeoutMs, cancellationToken);
                    var completed = await Task.WhenAny(connected.Task, timeout);
                    if (ReferenceEquals(completed, connected.Task))
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
                await Task.Delay(retryIntervalMs, cancellationToken);
            }

            return GetConnectedPort() == targetPort;
        }

        internal async Task SendAsync(object message, CancellationToken cancellationToken)
        {
            var socket = GetCurrentSocket();
            if (socket == null || socket.State != WebSocketState.Open)
            {
                throw new PluginException("ERR_UNITY_DISCONNECTED", "bridge socket is not connected");
            }

            var json = JsonUtil.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    throw new PluginException("ERR_UNITY_DISCONNECTED", "bridge socket is not connected");
                }

                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        internal void CloseCurrentSocket(string reason)
        {
            var socket = GetCurrentSocket();
            if (socket == null)
            {
                return;
            }

            SafeCloseSocket(socket, reason);
        }

        private async Task LifecycleLoopAsync(CancellationToken cancellationToken)
        {
            double reconnectBackoffMs = _reconnectInitialMs;

            while (!cancellationToken.IsCancellationRequested)
            {
                var port = _desiredPortProvider();
                try
                {
                    await RunSessionAsync(port, cancellationToken);
                    reconnectBackoffMs = _reconnectInitialMs;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _onSessionError(port, ex);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var delayMs = ApplyJitter(reconnectBackoffMs);
                reconnectBackoffMs = Math.Min(reconnectBackoffMs * _reconnectMultiplier, _reconnectMaxBackoffMs);

                var reconnectedImmediately = await WaitReconnectSignalOrDelayAsync(delayMs, cancellationToken);
                if (reconnectedImmediately)
                {
                    reconnectBackoffMs = _reconnectInitialMs;
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
            var uri = new Uri($"ws://{_host}:{port}{_unityWsPath}");

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token))
            {
                connectCts.CancelAfter(_connectTimeoutMs);
                await socket.ConnectAsync(uri, connectCts.Token);
            }

            lock (_socketGate)
            {
                _socket = socket;
            }

            lock (_gate)
            {
                _connectedPort = port;
            }

            await _onConnectedAsync(port, sessionCts.Token);
            RaiseConnectedPortChanged(port);

            try
            {
                await ReceiveLoopAsync(port, socket, sessionCts.Token);
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

                    if (ReferenceEquals(_sessionCts, sessionCts))
                    {
                        _sessionCts = null;
                    }
                }

                SafeCloseSocket(socket, "session-ended");
                _onDisconnected(port);
            }
        }

        private async Task ReceiveLoopAsync(int port, ClientWebSocket socket, CancellationToken cancellationToken)
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

                    if (stream.Length > _maxMessageBytes)
                    {
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
                await _onTextMessageAsync(port, raw, cancellationToken);
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

        private async Task<bool> WaitReconnectSignalOrDelayAsync(double delayMs, CancellationToken cancellationToken)
        {
            if (delayMs <= 0)
            {
                return true;
            }

            var waitSignalTask = _reconnectSignal.WaitAsync(cancellationToken);
            var delayTask = Task.Delay((int)delayMs, cancellationToken);

            var completed = await Task.WhenAny(waitSignalTask, delayTask);
            if (ReferenceEquals(completed, waitSignalTask))
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
                var jitter = (_random.NextDouble() * 2.0 - 1.0) * _reconnectJitterRatio;
                return Math.Max(0, baseMs * (1.0 + jitter));
            }
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

        private ClientWebSocket GetCurrentSocket()
        {
            lock (_socketGate)
            {
                return _socket;
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
    }
}
