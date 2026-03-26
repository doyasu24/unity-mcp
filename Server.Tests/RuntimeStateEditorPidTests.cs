using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class RuntimeStateEditorPidTests
{
    [Theory]
    [InlineData("12345:/Users/dev/MyProject/Assets", 12345)]
    [InlineData("99:/short/Assets", 99)]
    [InlineData("1:/a", 1)]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("nopid:/path/Assets", 0)]
    [InlineData("12345", 0)]
    [InlineData(":no_pid", 0)]
    public void ParseEditorPid_ReturnsExpected(string? input, int expected)
    {
        Assert.Equal(expected, RuntimeState.ParseEditorPid(input));
    }

    [Fact]
    public void GetEditorPid_ReturnsZero_WhenNotConnected()
    {
        var state = new RuntimeState();
        Assert.Equal(0, state.GetEditorPid());
    }

    [Fact]
    public void GetEditorPid_ReturnsPid_WhenConnected()
    {
        var state = new RuntimeState();
        state.OnConnected(EditorState.Ready, "conn-1", "42:/some/project/Assets");
        Assert.Equal(42, state.GetEditorPid());
    }

    [Fact]
    public void GetEditorPid_ReturnsZero_AfterDisconnect()
    {
        var state = new RuntimeState();
        state.OnConnected(EditorState.Ready, "conn-1", "42:/some/project/Assets");
        state.OnDisconnected();
        Assert.Equal(0, state.GetEditorPid());
    }
}
