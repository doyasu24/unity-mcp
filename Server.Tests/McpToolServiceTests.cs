using System.Text.Json.Nodes;
using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class McpToolServiceTests
{
    [Fact]
    public async Task CallToolAsync_ReturnsSnapshot_ForGetEditorState()
    {
        var runtimeState = new RuntimeState();
        runtimeState.SetServerState(ServerState.WaitingEditor);
        var service = CreateService(runtimeState);

        var result = await service.CallToolAsync("get_editor_state", new JsonObject(), CancellationToken.None);

        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal("waiting_editor", structured["server_state"]?.GetValue<string>());
        Assert.Equal("unknown", structured["editor_state"]?.GetValue<string>());
        Assert.False(structured["connected"]?.GetValue<bool>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsToolError_ForUnknownTool()
    {
        var service = CreateService(new RuntimeState());

        var result = await service.CallToolAsync("no_such_tool", new JsonObject(), CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.UnknownCommand, structured["code"]?.GetValue<string>());
    }

    private static McpToolService CreateService(RuntimeState runtimeState)
    {
        var scheduler = new RequestScheduler(Constants.QueueMaxSize);
        var bridge = new UnityBridge(runtimeState, scheduler);
        return new McpToolService(runtimeState, bridge);
    }
}
