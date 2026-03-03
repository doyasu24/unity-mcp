using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class ConnectionStabilitySoakTests
{
    [Fact]
    public void OneMinute_DisconnectInjection_WaitingReasonIsCorrect()
    {
        var runtimeState = new RuntimeState();
        var random = new Random(20260227);

        var seq = 1UL;
        var failures = 0;
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
                failures += 1;
            }

            if (!enteringCompile && waitingReason != "reconnecting")
            {
                failures += 1;
            }

            reconnectCount += 1;
        }

        Assert.Equal(0, failures);
    }

    [Fact]
    public void CompileTransition_ThenDisconnect_WaitingReasonIsCompiling()
    {
        var runtimeState = new RuntimeState();

        runtimeState.OnConnected(EditorState.Ready, "conn-1", "editor-1");
        runtimeState.OnEditorStatus(EditorState.Compiling, 1);
        runtimeState.OnDisconnected();

        var snapshot = runtimeState.GetSnapshot();
        Assert.Equal("compiling", snapshot.WaitingReason);
    }
}
