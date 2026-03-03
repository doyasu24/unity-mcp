using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class UnityBridgeShutdownTests
{
    [Fact]
    public void BeginShutdown_TransitionsRuntimeStateToDisconnected_AndIsIdempotent()
    {
        var runtimeState = new RuntimeState();
        runtimeState.OnConnected(EditorState.Ready, "conn-1", "editor-1");
        var bridge = new UnityBridge(runtimeState, new RequestScheduler(Constants.QueueMaxSize));

        bridge.BeginShutdown();
        bridge.BeginShutdown();

        var snapshot = runtimeState.GetSnapshot();
        Assert.Equal(ServerState.WaitingEditor.ToWire(), snapshot.ServerState);
        Assert.False(snapshot.Connected);
    }
}
