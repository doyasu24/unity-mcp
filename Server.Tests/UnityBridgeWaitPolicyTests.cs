using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class UnityBridgeWaitPolicyTests
{
    [Theory]
    [InlineData("compiling")]
    [InlineData("reloading")]
    public void ResolveEditorReadyWaitPolicy_UsesCompileGrace_ForCompileRelatedWaitingReason(string waitingReason)
    {
        var snapshot = Snapshot(waitingReason);

        var policy = UnityBridge.ResolveEditorReadyWaitPolicy(snapshot);

        Assert.Equal(TimeSpan.FromMilliseconds(Constants.CompileGraceTimeoutMs), policy.Timeout);
        Assert.Equal(ErrorCodes.CompileTimeout, policy.TimeoutErrorCode);
    }

    [Fact]
    public void ResolveEditorReadyWaitPolicy_UsesReconnectWait_ForOtherWaitingReasons()
    {
        var snapshot = Snapshot("reconnecting");

        var policy = UnityBridge.ResolveEditorReadyWaitPolicy(snapshot);

        Assert.Equal(TimeSpan.FromMilliseconds(Constants.RequestReconnectWaitMs), policy.Timeout);
        Assert.Equal(ErrorCodes.EditorNotReady, policy.TimeoutErrorCode);
    }

    private static RuntimeSnapshot Snapshot(string waitingReason)
    {
        return new RuntimeSnapshot(
            ServerState.WaitingEditor.ToWire(),
            EditorState.Unknown.ToWire(),
            false,
            0,
            waitingReason,
            null,
            null,
            null);
    }
}
