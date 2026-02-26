using System.Net.WebSockets;

namespace UnityMcpServer;

internal enum SessionPromotionResult
{
    Activated,
    AlreadyActive,
    RejectedActiveExists,
    UnknownSocket,
}

internal readonly record struct SessionRemovalResult(bool WasKnown, bool WasActive);

internal sealed class UnitySessionRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<WebSocket> _registeredSockets = new();
    private WebSocket? _activeSocket;

    public void Register(WebSocket socket)
    {
        lock (_gate)
        {
            _registeredSockets.Add(socket);
        }
    }

    public SessionPromotionResult TryPromote(WebSocket socket)
    {
        lock (_gate)
        {
            if (!_registeredSockets.Contains(socket))
            {
                return SessionPromotionResult.UnknownSocket;
            }

            if (_activeSocket is not null && _activeSocket.State != WebSocketState.Open)
            {
                _activeSocket = null;
            }

            if (ReferenceEquals(_activeSocket, socket))
            {
                return SessionPromotionResult.AlreadyActive;
            }

            if (_activeSocket is not null)
            {
                return SessionPromotionResult.RejectedActiveExists;
            }

            _activeSocket = socket;
            return SessionPromotionResult.Activated;
        }
    }

    public bool IsActive(WebSocket socket)
    {
        lock (_gate)
        {
            return ReferenceEquals(_activeSocket, socket);
        }
    }

    public WebSocket? GetActiveSocket()
    {
        lock (_gate)
        {
            if (_activeSocket is null || _activeSocket.State != WebSocketState.Open)
            {
                return null;
            }

            return _activeSocket;
        }
    }

    public SessionRemovalResult Remove(WebSocket socket)
    {
        lock (_gate)
        {
            var wasKnown = _registeredSockets.Remove(socket);
            var wasActive = ReferenceEquals(_activeSocket, socket);
            if (wasActive)
            {
                _activeSocket = null;
            }

            return new SessionRemovalResult(wasKnown, wasActive);
        }
    }

    public IReadOnlyList<WebSocket> DrainAll()
    {
        lock (_gate)
        {
            var sockets = _registeredSockets.ToList();
            _registeredSockets.Clear();
            _activeSocket = null;
            return sockets;
        }
    }
}
