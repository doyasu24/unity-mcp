using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class ConnectionStabilitySoakTests
{
    [Fact]
    public void OneMinute_DisconnectInjection_DoesNotTriggerFalseSingleMissFailure()
    {
        var runtimeState = new RuntimeState();
        var heartbeatMissState = new HeartbeatMissState(2);
        var random = new Random(20260227);

        var seq = 1UL;
        var falseFailures = 0;
        var reconnectCount = 0;
        var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        while (DateTimeOffset.UtcNow < until)
        {
            var connectionId = $"conn-{reconnectCount}";
            runtimeState.OnConnected(EditorState.Ready, connectionId, "editor-1");

            var enteringCompile = random.Next(0, 3) == 0;
            if (enteringCompile)
            {
                runtimeState.OnEditorStatus(EditorState.Compiling, seq++);
            }

            runtimeState.OnDisconnected();
            var waitingReason = runtimeState.GetSnapshot().WaitingReason;
            if (enteringCompile && waitingReason != "compiling")
            {
                falseFailures += 1;
            }

            if (!enteringCompile && waitingReason != "reconnecting")
            {
                falseFailures += 1;
            }

            var closedAfterSingleMiss = heartbeatMissState.RegisterProbeResult(false);
            if (closedAfterSingleMiss)
            {
                falseFailures += 1;
            }

            heartbeatMissState.RegisterProbeResult(true);
            reconnectCount += 1;
        }

        Assert.Equal(0, falseFailures);
    }
}
