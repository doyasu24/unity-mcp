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
    [InlineData("entering_play_mode", "entering_play_mode")]
    [InlineData("unknown-value", "unknown")]
    [InlineData(null, "unknown")]
    public void ParseEditorState_MapsExpectedValue(string? wireState, string expectedWire)
    {
        var parsed = WireState.ParseEditorState(wireState);
        Assert.Equal(expectedWire, parsed.ToWire());
    }

    [Fact]
    public void WaitingReason_EnteringPlayMode_ToWire_MapsExpectedValue()
    {
        Assert.Equal("entering_play_mode", WaitingReason.EnteringPlayMode.ToWire());
    }

}
