using System.Net.WebSockets;

namespace UnityMcpServer;

internal enum SessionPromotionResult
{
    Activated,
    AlreadyActive,
    ReplacedActiveSameEditor,
    RejectedActiveExists,
    UnknownSocket,
}

internal readonly record struct SessionPromotionOutcome(SessionPromotionResult Result, WebSocket? ReplacedSocket);

internal readonly record struct SessionRemovalResult(bool WasKnown, bool WasActive);

internal sealed class UnitySessionRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<WebSocket> _registeredSockets = new();
    private WebSocket? _activeSocket;
    private string? _activeEditorInstanceId;

    public void Register(WebSocket socket)
    {
        lock (_gate)
        {
            _registeredSockets.Add(socket);
        }
    }

    public SessionPromotionOutcome TryPromote(WebSocket socket, string? editorInstanceId)
    {
        var normalizedEditorInstanceId = NormalizeEditorInstanceId(editorInstanceId);
        lock (_gate)
        {
            if (!_registeredSockets.Contains(socket))
            {
                return new SessionPromotionOutcome(SessionPromotionResult.UnknownSocket, null);
            }

            if (_activeSocket is not null && _activeSocket.State != WebSocketState.Open)
            {
                _activeSocket = null;
                _activeEditorInstanceId = null;
            }

            if (ReferenceEquals(_activeSocket, socket))
            {
                _activeEditorInstanceId = normalizedEditorInstanceId;
                return new SessionPromotionOutcome(SessionPromotionResult.AlreadyActive, null);
            }

            if (_activeSocket is not null)
            {
                if (IsSameEditorInstance(normalizedEditorInstanceId))
                {
                    var replacedSocket = _activeSocket;
                    _activeSocket = socket;
                    _activeEditorInstanceId = normalizedEditorInstanceId;
                    return new SessionPromotionOutcome(SessionPromotionResult.ReplacedActiveSameEditor, replacedSocket);
                }

                return new SessionPromotionOutcome(SessionPromotionResult.RejectedActiveExists, null);
            }

            _activeSocket = socket;
            _activeEditorInstanceId = normalizedEditorInstanceId;
            return new SessionPromotionOutcome(SessionPromotionResult.Activated, null);
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
                _activeEditorInstanceId = null;
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
            _activeEditorInstanceId = null;
            return sockets;
        }
    }

    private bool IsSameEditorInstance(string? editorInstanceId)
    {
        return !string.IsNullOrWhiteSpace(_activeEditorInstanceId)
            && !string.IsNullOrWhiteSpace(editorInstanceId)
            && string.Equals(_activeEditorInstanceId, editorInstanceId, StringComparison.Ordinal);
    }

    private static string? NormalizeEditorInstanceId(string? editorInstanceId)
    {
        return string.IsNullOrWhiteSpace(editorInstanceId)
            ? null
            : editorInstanceId.Trim();
    }
}
