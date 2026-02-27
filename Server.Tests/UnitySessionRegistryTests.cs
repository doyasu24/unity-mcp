using System.Net.WebSockets;
using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class UnitySessionRegistryTests
{
    [Fact]
    public void TryAccept_ActivatesFirstSocket()
    {
        var registry = new UnitySessionRegistry();
        var socket = new FakeWebSocket();

        var result = registry.TryAccept(socket, "editor-a");

        Assert.Equal(AcceptResult.Accepted, result.Result);
        Assert.Null(result.ReplacedSocket);
        Assert.True(registry.IsActive(socket));
        Assert.Same(socket, registry.GetActiveSocket());
    }

    [Fact]
    public void TryAccept_RejectsDifferentEditor_WhenActiveIsOpen()
    {
        var registry = new UnitySessionRegistry();
        var first = new FakeWebSocket();
        var second = new FakeWebSocket();
        registry.TryAccept(first, "editor-a");

        var result = registry.TryAccept(second, "editor-b");

        Assert.Equal(AcceptResult.Rejected, result.Result);
        Assert.Null(result.ReplacedSocket);
        Assert.True(registry.IsActive(first));
        Assert.False(registry.IsActive(second));
    }

    [Fact]
    public void TryAccept_ReplacesSameEditor()
    {
        var registry = new UnitySessionRegistry();
        var first = new FakeWebSocket();
        var second = new FakeWebSocket();
        registry.TryAccept(first, "editor-a");

        var result = registry.TryAccept(second, "editor-a");

        Assert.Equal(AcceptResult.Replaced, result.Result);
        Assert.Same(first, result.ReplacedSocket);
        Assert.False(registry.IsActive(first));
        Assert.True(registry.IsActive(second));
        Assert.Same(second, registry.GetActiveSocket());
    }

    [Fact]
    public void TryAccept_AcceptsNewSocket_WhenExistingActiveIsClosed()
    {
        var registry = new UnitySessionRegistry();
        var first = new FakeWebSocket();
        var second = new FakeWebSocket();
        registry.TryAccept(first, "editor-a");
        first.SetState(WebSocketState.Closed);

        var result = registry.TryAccept(second, "editor-b");

        Assert.Equal(AcceptResult.Accepted, result.Result);
        Assert.Null(result.ReplacedSocket);
        Assert.True(registry.IsActive(second));
        Assert.False(registry.IsActive(first));
    }

    [Fact]
    public void Remove_ReturnsTrueForActiveSocket()
    {
        var registry = new UnitySessionRegistry();
        var socket = new FakeWebSocket();
        registry.TryAccept(socket, "editor-a");

        var wasActive = registry.Remove(socket);

        Assert.True(wasActive);
        Assert.Null(registry.GetActiveSocket());
    }

    [Fact]
    public void Remove_ReturnsFalseForNonActiveSocket()
    {
        var registry = new UnitySessionRegistry();
        var active = new FakeWebSocket();
        var other = new FakeWebSocket();
        registry.TryAccept(active, "editor-a");

        var wasActive = registry.Remove(other);

        Assert.False(wasActive);
        Assert.True(registry.IsActive(active));
    }

    [Fact]
    public void DrainAll_ClearsActiveSocket()
    {
        var registry = new UnitySessionRegistry();
        var socket = new FakeWebSocket();
        registry.TryAccept(socket, "editor-a");

        var drained = registry.DrainAll();

        Assert.Single(drained);
        Assert.Contains(socket, drained);
        Assert.Null(registry.GetActiveSocket());
        Assert.False(registry.IsActive(socket));
    }

    [Fact]
    public void DrainAll_ReturnsEmpty_WhenNoActiveSocket()
    {
        var registry = new UnitySessionRegistry();

        var drained = registry.DrainAll();

        Assert.Empty(drained);
    }

    [Fact]
    public void TryAccept_ReturnAccepted_WhenSameSocketReAccepted()
    {
        var registry = new UnitySessionRegistry();
        var socket = new FakeWebSocket();
        registry.TryAccept(socket, "editor-a");

        var result = registry.TryAccept(socket, "editor-a");

        Assert.Equal(AcceptResult.Accepted, result.Result);
        Assert.Null(result.ReplacedSocket);
        Assert.True(registry.IsActive(socket));
    }

    private sealed class FakeWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public void SetState(WebSocketState state)
        {
            _state = state;
        }

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }
}
