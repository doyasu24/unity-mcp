using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class RuntimeStateTests
{
    [Fact]
    public void OnConnected_PopulatesConnectionFields_AndClearsWaitingReason()
    {
        var runtimeState = new RuntimeState();

        runtimeState.OnConnected(EditorState.Ready, "conn-1", "editor-1");
        var snapshot = runtimeState.GetSnapshot();

        Assert.True(snapshot.Connected);
        Assert.Equal("none", snapshot.WaitingReason);
        Assert.Equal("conn-1", snapshot.ActiveConnectionId);
        Assert.Equal("editor-1", snapshot.EditorInstanceId);
    }

    [Fact]
    public void OnDisconnected_FromCompiling_KeepsCompileWaitingReason()
    {
        var runtimeState = new RuntimeState();
        runtimeState.OnConnected(EditorState.Ready, "conn-1", "editor-1");
        runtimeState.OnEditorStatus(EditorState.Compiling, 1);

        runtimeState.OnDisconnected();
        var snapshot = runtimeState.GetSnapshot();

        Assert.False(snapshot.Connected);
        Assert.Equal("compiling", snapshot.WaitingReason);
    }

    [Fact]
    public void OnDisconnected_FromReady_UsesReconnectingWaitingReason()
    {
        var runtimeState = new RuntimeState();
        runtimeState.OnConnected(EditorState.Ready, "conn-1", "editor-1");

        runtimeState.OnDisconnected();
        var snapshot = runtimeState.GetSnapshot();

        Assert.False(snapshot.Connected);
        Assert.Equal("reconnecting", snapshot.WaitingReason);
    }
}
