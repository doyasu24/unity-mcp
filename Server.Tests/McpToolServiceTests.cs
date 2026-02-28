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

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForGetSceneHierarchy_InvalidMaxDepth()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["max_depth"] = 999 };
        var result = await service.CallToolAsync(ToolNames.GetSceneHierarchy, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForGetComponentInfo_MissingGameObjectPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["index"] = 0 };
        var result = await service.CallToolAsync(ToolNames.GetSceneComponentInfo, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForGetComponentInfo_MissingIndex()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["game_object_path"] = "/Player" };
        var result = await service.CallToolAsync(ToolNames.GetSceneComponentInfo, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageComponent_InvalidAction()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "invalid",
            ["game_object_path"] = "/Player",
        };
        var result = await service.CallToolAsync(ToolNames.ManageSceneComponent, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageComponent_AddMissingComponentType()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "add",
            ["game_object_path"] = "/Player",
        };
        var result = await service.CallToolAsync(ToolNames.ManageSceneComponent, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageComponent_UpdateMissingIndex()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "update",
            ["game_object_path"] = "/Player",
            ["fields"] = new JsonObject { ["speed"] = 5 },
        };
        var result = await service.CallToolAsync(ToolNames.ManageSceneComponent, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageComponent_MoveMissingNewIndex()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "move",
            ["game_object_path"] = "/Player",
            ["index"] = 1,
        };
        var result = await service.CallToolAsync(ToolNames.ManageSceneComponent, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForGetPrefabHierarchy_MissingPrefabPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject();
        var result = await service.CallToolAsync(ToolNames.GetPrefabHierarchy, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForGetPrefabComponentInfo_MissingPrefabPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["game_object_path"] = "", ["index"] = 0 };
        var result = await service.CallToolAsync(ToolNames.GetPrefabComponentInfo, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForGetPrefabComponentInfo_MissingIndex()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["game_object_path"] = "/Child",
        };
        var result = await service.CallToolAsync(ToolNames.GetPrefabComponentInfo, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManagePrefabComponent_InvalidAction()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["action"] = "invalid",
            ["game_object_path"] = "/Child",
        };
        var result = await service.CallToolAsync(ToolNames.ManagePrefabComponent, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManagePrefabComponent_AddMissingComponentType()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["action"] = "add",
            ["game_object_path"] = "/Child",
        };
        var result = await service.CallToolAsync(ToolNames.ManagePrefabComponent, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManagePrefabComponent_MoveMissingNewIndex()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["action"] = "move",
            ["game_object_path"] = "/Child",
            ["index"] = 1,
        };
        var result = await service.CallToolAsync(ToolNames.ManagePrefabComponent, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    private static McpToolService CreateService(RuntimeState runtimeState)
    {
        var scheduler = new RequestScheduler(Constants.QueueMaxSize);
        var bridge = new UnityBridge(runtimeState, scheduler);
        return new McpToolService(runtimeState, bridge);
    }
}
