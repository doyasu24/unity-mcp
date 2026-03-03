using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class WireStateTests
{
    [Theory]
    [InlineData("booting")]
    [InlineData("waiting_editor")]
    [InlineData("ready")]
    [InlineData("stopping")]
    [InlineData("stopped")]
    public void ServerState_ToWire_MapsExpectedValue(string expected)
    {
        var state = expected switch
        {
            "booting" => ServerState.Booting,
            "waiting_editor" => ServerState.WaitingEditor,
            "ready" => ServerState.Ready,
            "stopping" => ServerState.Stopping,
            "stopped" => ServerState.Stopped,
            _ => throw new InvalidOperationException("Unexpected test input"),
        };

        Assert.Equal(expected, state.ToWire());
    }

    [Theory]
    [InlineData("ready", "ready")]
    [InlineData("compiling", "compiling")]
    [InlineData("reloading", "reloading")]
    [InlineData("unknown-value", "unknown")]
    [InlineData(null, "unknown")]
    public void ParseEditorState_MapsExpectedValue(string? wireState, string expectedWire)
    {
        var parsed = WireState.ParseEditorState(wireState);
        Assert.Equal(expectedWire, parsed.ToWire());
    }

    [Fact]
    public void TryParseJobState_ReturnsFalse_ForUnknownState()
    {
        var parsed = WireState.TryParseJobState("something-else", out var state);

        Assert.False(parsed);
        Assert.Equal(JobState.Failed, state);
    }

    [Theory]
    [InlineData("succeeded", true)]
    [InlineData("failed", true)]
    [InlineData("timeout", true)]
    [InlineData("cancelled", true)]
    [InlineData("queued", false)]
    [InlineData("running", false)]
    public void IsTerminal_ReturnsExpectedValue(string wireState, bool expected)
    {
        var parsed = WireState.TryParseJobState(wireState, out var state);
        Assert.True(parsed);
        Assert.Equal(expected, WireState.IsTerminal(state));
    }
}
