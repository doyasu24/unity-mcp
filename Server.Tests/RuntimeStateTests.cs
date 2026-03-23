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

    [Fact]
    public void OnDisconnected_FromEnteringPlayMode_KeepsEnteringPlayModeWaitingReason()
    {
        var runtimeState = new RuntimeState();
        runtimeState.OnConnected(EditorState.Ready, "conn-1", "editor-1");
        runtimeState.OnEditorStatus(EditorState.EnteringPlayMode, 1);

        runtimeState.OnDisconnected();
        var snapshot = runtimeState.GetSnapshot();

        Assert.False(snapshot.Connected);
        Assert.Equal("entering_play_mode", snapshot.WaitingReason);
    }

    [Fact]
    public void OnDisconnected_FromExitingPlayMode_KeepsExitingPlayModeWaitingReason()
    {
        var runtimeState = new RuntimeState();
        runtimeState.OnConnected(EditorState.Ready, "conn-1", "editor-1");
        runtimeState.OnEditorStatus(EditorState.ExitingPlayMode, 1);

        runtimeState.OnDisconnected();
        var snapshot = runtimeState.GetSnapshot();

        Assert.False(snapshot.Connected);
        Assert.Equal("exiting_play_mode", snapshot.WaitingReason);
    }

    [Fact]
    public async Task WaitForStateTransitionAsync_ReturnsFalse_WhenNoTransitionWithinWindow()
    {
        var runtimeState = new RuntimeState();
        runtimeState.OnConnected(EditorState.Ready, "conn-1", "editor-1");

        var result = await runtimeState.WaitForStateTransitionAsync(
            TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task WaitForStateTransitionAsync_ReturnsTrue_WhenEditorTransitionsToCompiling()
    {
        var runtimeState = new RuntimeState();
        runtimeState.OnConnected(EditorState.Ready, "conn-1", "editor-1");

        var waitTask = runtimeState.WaitForStateTransitionAsync(
            TimeSpan.FromMilliseconds(500), CancellationToken.None);

        // コンパイル開始をシミュレート
        await Task.Delay(30);
        runtimeState.OnEditorStatus(EditorState.Compiling, 1);

        var result = await waitTask;
        Assert.True(result);
    }

    [Fact]
    public async Task WaitForStateTransitionAsync_ReturnsTrue_WhenAlreadyNotReady()
    {
        var runtimeState = new RuntimeState();
        runtimeState.OnConnected(EditorState.Ready, "conn-1", "editor-1");
        runtimeState.OnEditorStatus(EditorState.Compiling, 1);

        var result = await runtimeState.WaitForStateTransitionAsync(
            TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task WaitForStateTransitionAsync_ReturnsTrue_WhenDisconnected()
    {
        var runtimeState = new RuntimeState();
        runtimeState.OnConnected(EditorState.Ready, "conn-1", "editor-1");

        var waitTask = runtimeState.WaitForStateTransitionAsync(
            TimeSpan.FromMilliseconds(500), CancellationToken.None);

        await Task.Delay(30);
        runtimeState.OnDisconnected();

        var result = await waitTask;
        Assert.True(result);
    }

    [Fact]
    public async Task WaitForStateTransitionAsync_ReturnsTrue_WhenTransitionDuringSetup()
    {
        // Handler 登録直後の再チェック（post-subscribe double-check）を検証。
        // 別スレッドから遷移を発火させ、Handler 経由ではなく
        // subscribe 後の IsEditorReady() 再チェックで検知されるケースをカバーする。
        var runtimeState = new RuntimeState();
        runtimeState.OnConnected(EditorState.Ready, "conn-1", "editor-1");

        // Task.Run で並行して状態遷移を発火。初回 IsEditorReady() チェック通過後に
        // 到着する可能性がある。
        _ = Task.Run(async () =>
        {
            await Task.Yield();
            runtimeState.OnEditorStatus(EditorState.Compiling, 1);
        });

        var result = await runtimeState.WaitForStateTransitionAsync(
            TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.True(result);
    }
}
