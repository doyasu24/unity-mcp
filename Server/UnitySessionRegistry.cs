using System.Net.WebSockets;

namespace UnityMcpServer;

internal enum AcceptResult
{
    Accepted,
    Replaced,
    Rejected,
}

internal readonly record struct AcceptOutcome(AcceptResult Result, WebSocket? ReplacedSocket);

internal sealed class UnitySessionRegistry
{
    private readonly object _gate = new();
    private WebSocket? _activeSocket;
    private string? _activeEditorInstanceId;

    public AcceptOutcome TryAccept(WebSocket socket, string? editorInstanceId)
    {
        var normalizedEditorInstanceId = NormalizeEditorInstanceId(editorInstanceId);
        lock (_gate)
        {
            if (_activeSocket is not null && _activeSocket.State != WebSocketState.Open)
            {
                _activeSocket = null;
                _activeEditorInstanceId = null;
            }

            if (ReferenceEquals(_activeSocket, socket))
            {
                _activeEditorInstanceId = normalizedEditorInstanceId;
                return new AcceptOutcome(AcceptResult.Accepted, null);
            }

            if (_activeSocket is not null)
            {
                if (IsSameEditorInstance(normalizedEditorInstanceId))
                {
                    var replacedSocket = _activeSocket;
                    _activeSocket = socket;
                    _activeEditorInstanceId = normalizedEditorInstanceId;
                    return new AcceptOutcome(AcceptResult.Replaced, replacedSocket);
                }

                return new AcceptOutcome(AcceptResult.Rejected, null);
            }

            _activeSocket = socket;
            _activeEditorInstanceId = normalizedEditorInstanceId;
            return new AcceptOutcome(AcceptResult.Accepted, null);
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

    public bool Remove(WebSocket socket)
    {
        lock (_gate)
        {
            var wasActive = ReferenceEquals(_activeSocket, socket);
            if (wasActive)
            {
                _activeSocket = null;
                _activeEditorInstanceId = null;
            }

            return wasActive;
        }
    }

    public IReadOnlyList<WebSocket> DrainAll()
    {
        lock (_gate)
        {
            var sockets = new List<WebSocket>();
            if (_activeSocket is not null)
            {
                sockets.Add(_activeSocket);
            }

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
