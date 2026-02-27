using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class HeartbeatMissStateTests
{
    [Fact]
    public void RegisterProbeResult_ClosesOnlyAfterThreshold()
    {
        var state = new HeartbeatMissState(2);

        var shouldCloseAfterFirstMiss = state.RegisterProbeResult(false);
        var shouldCloseAfterSecondMiss = state.RegisterProbeResult(false);

        Assert.False(shouldCloseAfterFirstMiss);
        Assert.True(shouldCloseAfterSecondMiss);
    }

    [Fact]
    public void RegisterProbeResult_ResetsMissCount_OnPong()
    {
        var state = new HeartbeatMissState(2);
        state.RegisterProbeResult(false);

        var shouldCloseAfterPong = state.RegisterProbeResult(true);
        var shouldCloseAfterMiss = state.RegisterProbeResult(false);

        Assert.False(shouldCloseAfterPong);
        Assert.False(shouldCloseAfterMiss);
        Assert.Equal(1, state.Misses);
    }
}
