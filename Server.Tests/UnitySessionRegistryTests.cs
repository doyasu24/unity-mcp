using System.Net.WebSockets;
using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class UnitySessionRegistryTests
{
    [Fact]
    public void TryPromote_ReturnsUnknown_WhenSocketIsNotRegistered()
    {
        var registry = new UnitySessionRegistry();
        var socket = new FakeWebSocket();

        var result = registry.TryPromote(socket, "editor-a");

        Assert.Equal(SessionPromotionResult.UnknownSocket, result.Result);
        Assert.Null(result.ReplacedSocket);
    }

    [Fact]
    public void TryPromote_ActivatesFirstRegisteredSocket()
    {
        var registry = new UnitySessionRegistry();
        var socket = new FakeWebSocket();
        registry.Register(socket);

        var result = registry.TryPromote(socket, "editor-a");

        Assert.Equal(SessionPromotionResult.Activated, result.Result);
        Assert.Null(result.ReplacedSocket);
        Assert.True(registry.IsActive(socket));
        Assert.Same(socket, registry.GetActiveSocket());
    }

    [Fact]
    public void TryPromote_RejectsAnotherSocket_WhenActiveIsOpen()
    {
        var registry = new UnitySessionRegistry();
        var first = new FakeWebSocket();
        var second = new FakeWebSocket();
        registry.Register(first);
        registry.Register(second);
        registry.TryPromote(first, "editor-a");

        var result = registry.TryPromote(second, "editor-b");

        Assert.Equal(SessionPromotionResult.RejectedActiveExists, result.Result);
        Assert.Null(result.ReplacedSocket);
        Assert.True(registry.IsActive(first));
        Assert.False(registry.IsActive(second));
    }

    [Fact]
    public void TryPromote_ReplacesActiveSocket_WhenEditorInstanceIdMatches()
    {
        var registry = new UnitySessionRegistry();
        var first = new FakeWebSocket();
        var second = new FakeWebSocket();
        registry.Register(first);
        registry.Register(second);
        registry.TryPromote(first, "editor-a");

        var result = registry.TryPromote(second, "editor-a");

        Assert.Equal(SessionPromotionResult.ReplacedActiveSameEditor, result.Result);
        Assert.Same(first, result.ReplacedSocket);
        Assert.False(registry.IsActive(first));
        Assert.True(registry.IsActive(second));
        Assert.Same(second, registry.GetActiveSocket());
    }

    [Fact]
    public void TryPromote_ActivatesNewSocket_WhenExistingActiveIsClosed()
    {
        var registry = new UnitySessionRegistry();
        var first = new FakeWebSocket();
        var second = new FakeWebSocket();
        registry.Register(first);
        registry.Register(second);
        registry.TryPromote(first, "editor-a");
        first.SetState(WebSocketState.Closed);

        var result = registry.TryPromote(second, "editor-b");

        Assert.Equal(SessionPromotionResult.Activated, result.Result);
        Assert.Null(result.ReplacedSocket);
        Assert.True(registry.IsActive(second));
        Assert.False(registry.IsActive(first));
    }

    [Fact]
    public void DrainAll_ClearsRegisteredAndActiveSockets()
    {
        var registry = new UnitySessionRegistry();
        var first = new FakeWebSocket();
        var second = new FakeWebSocket();
        registry.Register(first);
        registry.Register(second);
        registry.TryPromote(first, "editor-a");

        var drained = registry.DrainAll();

        Assert.Equal(2, drained.Count);
        Assert.Contains(first, drained);
        Assert.Contains(second, drained);
        Assert.Null(registry.GetActiveSocket());
        Assert.False(registry.IsActive(first));
        Assert.False(registry.IsActive(second));
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
