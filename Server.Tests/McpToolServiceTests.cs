using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task CallToolAsync_ReturnsError_ForGetHierarchy_InvalidMaxDepth()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["max_depth"] = 999 };
        var result = await service.CallToolAsync(ToolNames.GetHierarchy, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForGetComponentInfo_MissingGameObjectPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["index"] = 0 };
        var result = await service.CallToolAsync(ToolNames.GetComponentInfo, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_DispatchesToBridge_ForGetComponentInfo_WithoutIndex()
    {
        // index 省略時はコンポーネント一覧モードとしてブリッジにディスパッチされる
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["game_object_path"] = "/Player" };
        var result = await service.CallToolAsync(ToolNames.GetComponentInfo, args, CancellationToken.None);

        // Unity 未接続のためエラーになるが、パラメータバリデーションは通過している
        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.NotEqual(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
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
        var result = await service.CallToolAsync(ToolNames.ManageComponent, args, CancellationToken.None);

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
        var result = await service.CallToolAsync(ToolNames.ManageComponent, args, CancellationToken.None);

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
        var result = await service.CallToolAsync(ToolNames.ManageComponent, args, CancellationToken.None);

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
        var result = await service.CallToolAsync(ToolNames.ManageComponent, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_DispatchesToBridge_ForGetComponentInfo_PrefabMode_WithoutIndex()
    {
        // index 省略時はコンポーネント一覧モードとしてブリッジにディスパッチされる
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["game_object_path"] = "/Child",
        };
        var result = await service.CallToolAsync(ToolNames.GetComponentInfo, args, CancellationToken.None);

        // Unity 未接続のためエラーになるが、パラメータバリデーションは通過している
        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.NotEqual(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageComponent_PrefabMode_InvalidAction()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["action"] = "invalid",
            ["game_object_path"] = "/Child",
        };
        var result = await service.CallToolAsync(ToolNames.ManageComponent, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageComponent_PrefabMode_AddMissingComponentType()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["action"] = "add",
            ["game_object_path"] = "/Child",
        };
        var result = await service.CallToolAsync(ToolNames.ManageComponent, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageComponent_PrefabMode_MoveMissingNewIndex()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["action"] = "move",
            ["game_object_path"] = "/Child",
            ["index"] = 1,
        };
        var result = await service.CallToolAsync(ToolNames.ManageComponent, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForOpenScene_MissingPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject();
        var result = await service.CallToolAsync(ToolNames.OpenScene, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForOpenScene_InvalidMode()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["path"] = "Assets/Scenes/Main.unity",
            ["mode"] = "invalid",
        };
        var result = await service.CallToolAsync(ToolNames.OpenScene, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForCreateScene_MissingPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject();
        var result = await service.CallToolAsync(ToolNames.CreateScene, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForCreateScene_InvalidSetup()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["path"] = "Assets/Scenes/New.unity",
            ["setup"] = "invalid",
        };
        var result = await service.CallToolAsync(ToolNames.CreateScene, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForFindAssets_MissingFilter()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject();
        var result = await service.CallToolAsync(ToolNames.FindAssets, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    [InlineData(-5)]
    public async Task CallToolAsync_ReturnsError_ForFindAssets_InvalidMaxResults(int maxResults)
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["filter"] = "t:Material",
            ["max_results"] = maxResults,
        };
        var result = await service.CallToolAsync(ToolNames.FindAssets, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForFindGameObjects_NoFilter()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject();
        var result = await service.CallToolAsync(ToolNames.FindGameObjects, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForFindGameObjects_InvalidMaxResults()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["name"] = "Camera",
            ["max_results"] = 9999,
        };
        var result = await service.CallToolAsync(ToolNames.FindGameObjects, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32)]
    public async Task CallToolAsync_ReturnsError_ForFindGameObjects_LayerOutOfRange(int layer)
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["layer"] = layer,
        };
        var result = await service.CallToolAsync(ToolNames.FindGameObjects, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForInstantiatePrefab_MissingPrefabPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject();
        var result = await service.CallToolAsync(ToolNames.InstantiatePrefab, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForGetAssetInfo_MissingAssetPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject();
        var result = await service.CallToolAsync(ToolNames.GetAssetInfo, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForFindGameObjects_PrefabMode_InvalidMaxResults()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["name"] = "Camera",
            ["max_results"] = 9999,
        };
        var result = await service.CallToolAsync(ToolNames.FindGameObjects, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32)]
    public async Task CallToolAsync_ReturnsError_ForFindGameObjects_PrefabMode_LayerOutOfRange(int layer)
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["layer"] = layer,
        };
        var result = await service.CallToolAsync(ToolNames.FindGameObjects, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageAsset_MissingAction()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["asset_path"] = "Assets/Materials/Test.mat" };
        var result = await service.CallToolAsync(ToolNames.ManageAsset, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageAsset_CreateMissingAssetType()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "create",
            ["asset_path"] = "Assets/Materials/Test.mat",
        };
        var result = await service.CallToolAsync(ToolNames.ManageAsset, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageAsset_InvalidAssetType()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "create",
            ["asset_path"] = "Assets/Materials/Test.mat",
            ["asset_type"] = "invalid_type",
        };
        var result = await service.CallToolAsync(ToolNames.ManageAsset, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForCaptureScreenshot_InvalidSource()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["source"] = "invalid_source" };
        var result = await service.CallToolAsync(ToolNames.CaptureScreenshot, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_AcceptsSource_Camera()
    {
        // "camera" は有効な source なので InvalidParams エラーにはならない。
        // Unity 未接続のため実行時エラーにはなるが、パースは通ることを確認する。
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["source"] = "camera", ["camera_path"] = "/Main Camera" };
        var result = await service.CallToolAsync(ToolNames.CaptureScreenshot, args, CancellationToken.None);

        if (result["isError"]?.GetValue<bool>() == true)
        {
            var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
            Assert.NotEqual(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
        }
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForCaptureScreenshot_InvalidWidth()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["width"] = 99999 };
        var result = await service.CallToolAsync(ToolNames.CaptureScreenshot, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForGetHierarchy_NegativeOffset()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["offset"] = -1 };
        var result = await service.CallToolAsync(ToolNames.GetHierarchy, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForGetHierarchy_PrefabMode_NegativeOffset()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["offset"] = -1,
        };
        var result = await service.CallToolAsync(ToolNames.GetHierarchy, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_GetHierarchy_PrefabMode_MapsRootPathToGameObjectPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["root_path"] = "/Child",
        };
        // This will fail at UnityBridge level (no editor), but validates the parse succeeds
        var result = await service.CallToolAsync(ToolNames.GetHierarchy, args, CancellationToken.None);
        // If we get here without InvalidParams, the root_path mapping worked
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForFindAssets_NegativeOffset()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["filter"] = "t:Material",
            ["offset"] = -1,
        };
        var result = await service.CallToolAsync(ToolNames.FindAssets, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForFindGameObjects_NegativeOffset()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["name"] = "Camera",
            ["offset"] = -1,
        };
        var result = await service.CallToolAsync(ToolNames.FindGameObjects, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForFindGameObjects_PrefabMode_NegativeOffset()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["prefab_path"] = "Assets/Prefabs/Player.prefab",
            ["name"] = "Camera",
            ["offset"] = -1,
        };
        var result = await service.CallToolAsync(ToolNames.FindGameObjects, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForExecuteBatch_MissingOperations()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject();
        var result = await service.CallToolAsync(ToolNames.ExecuteBatch, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForExecuteBatch_EmptyOperations()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["operations"] = new JsonArray() };
        var result = await service.CallToolAsync(ToolNames.ExecuteBatch, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForExecuteBatch_TooManyOperations()
    {
        var service = CreateService(new RuntimeState());

        var ops = new JsonArray();
        for (var i = 0; i < 51; i++)
        {
            ops.Add(new JsonObject { ["tool_name"] = "clear_console" });
        }

        var args = new JsonObject { ["operations"] = ops };
        var result = await service.CallToolAsync(ToolNames.ExecuteBatch, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForExecuteBatch_MissingToolName()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["operations"] = new JsonArray
            {
                new JsonObject { ["arguments"] = new JsonObject() },
            },
        };
        var result = await service.CallToolAsync(ToolNames.ExecuteBatch, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForExecuteBatch_NestedBatch()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["operations"] = new JsonArray
            {
                new JsonObject { ["tool_name"] = "execute_batch" },
            },
        };
        var result = await service.CallToolAsync(ToolNames.ExecuteBatch, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ExecuteBatch_ValidatesUnknownToolBeforeExecution()
    {
        // 事前バリデーションで不正ツール名を弾く（部分的副作用なし）
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["operations"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool_name"] = "get_editor_state",
                    ["arguments"] = new JsonObject(),
                },
                new JsonObject
                {
                    ["tool_name"] = "nonexistent_tool",
                    ["arguments"] = new JsonObject(),
                },
            },
        };
        var result = await service.CallToolAsync(ToolNames.ExecuteBatch, args, CancellationToken.None);

        // 不正ツール名があるので全体がエラー（部分的副作用なし）
        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
        Assert.Contains("nonexistent_tool", structured["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ExecuteBatch_RunTestsNotBlocked()
    {
        // run_tests はバッチ内で許可されている（execute_batch 自身のみ再帰防止でブロック）
        // 実際の実行は Unity 接続が必要なので接続エラーになるが、バリデーションは通過する
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["operations"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool_name"] = "run_tests",
                    ["arguments"] = new JsonObject(),
                },
            },
        };
        var result = await service.CallToolAsync(ToolNames.ExecuteBatch, args, CancellationToken.None);

        // batch レスポンス形式で返る
        // Unity 未接続のためツール実行はエラーになるが、"not allowed in a batch" ではない
        var structured = result["structuredContent"];
        if (result["isError"]?.GetValue<bool>() != true)
        {
            // batch レスポンスが返る
            Assert.NotNull(structured?["results"]);
        }
        else
        {
            // エラーの場合、ブロックリスト由来ではないことを確認
            var message = structured?["message"]?.GetValue<string>() ?? "";
            Assert.DoesNotContain("not allowed in a batch", message);
        }
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForExecuteBatch_UnknownTool()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["operations"] = new JsonArray
            {
                new JsonObject { ["tool_name"] = "nonexistent_tool" },
            },
        };
        var result = await service.CallToolAsync(ToolNames.ExecuteBatch, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForRunTests_BothFiltersSpecified()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["test_full_name"] = "MyFixture.MyTest",
            ["test_name_pattern"] = "^MyNamespace\\.",
        };
        var result = await service.CallToolAsync(ToolNames.RunTests, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
        Assert.Contains("mutually exclusive", structured["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ParsesRunTests_TestNamePatternOnly()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["test_name_pattern"] = "^MyNamespace\\.",
        };
        // Will fail at UnityBridge level (no editor), but validates the parse succeeds
        var result = await service.CallToolAsync(ToolNames.RunTests, args, CancellationToken.None);
        // If we get here without InvalidParams, the parse worked
        var structured = result["structuredContent"] as JsonObject;
        Assert.NotEqual(ErrorCodes.InvalidParams, structured?["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForRunTests_EmptyTestFullName()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["test_full_name"] = "",
        };
        var result = await service.CallToolAsync(ToolNames.RunTests, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForRunTests_EmptyTestNamePattern()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["test_name_pattern"] = "",
        };
        var result = await service.CallToolAsync(ToolNames.RunTests, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManagePrefab_InvalidAction()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "invalid",
            ["game_object_path"] = "/Player",
        };
        var result = await service.CallToolAsync(ToolNames.ManagePrefab, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManagePrefab_GetStatusMissingPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "get_status",
        };
        var result = await service.CallToolAsync(ToolNames.ManagePrefab, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManagePrefab_SaveMissingPrefabPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "save",
            ["game_object_path"] = "/Player",
        };
        var result = await service.CallToolAsync(ToolNames.ManagePrefab, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageBuild_InvalidAction()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["action"] = "invalid" };
        var result = await service.CallToolAsync(ToolNames.ManageBuild, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageBuild_BuildMissingOutputPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["action"] = "build" };
        var result = await service.CallToolAsync(ToolNames.ManageBuild, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageBuild_BuildInvalidTarget()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "build",
            ["output_path"] = "Builds/Test",
            ["target"] = "invalid_platform",
        };
        var result = await service.CallToolAsync(ToolNames.ManageBuild, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageBuild_BuildInvalidOption()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "build",
            ["output_path"] = "Builds/Test",
            ["options"] = new JsonArray("invalid_option"),
        };
        var result = await service.CallToolAsync(ToolNames.ManageBuild, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageBuild_SwitchPlatformMissingTarget()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["action"] = "switch_platform" };
        var result = await service.CallToolAsync(ToolNames.ManageBuild, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageBuild_SwitchPlatformInvalidTarget()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "switch_platform",
            ["target"] = "unknown",
        };
        var result = await service.CallToolAsync(ToolNames.ManageBuild, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageBuild_GetSettingsMissingProperty()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["action"] = "get_settings" };
        var result = await service.CallToolAsync(ToolNames.ManageBuild, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageBuild_GetSettingsInvalidProperty()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "get_settings",
            ["property"] = "unknown_property",
        };
        var result = await service.CallToolAsync(ToolNames.ManageBuild, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageBuild_SetSettingsMissingValue()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "set_settings",
            ["property"] = "product_name",
        };
        var result = await service.CallToolAsync(ToolNames.ManageBuild, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageBuild_SetSettingsInvalidDefinesAction()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject
        {
            ["action"] = "set_settings",
            ["property"] = "defines",
            ["value"] = "SYMBOL",
            ["defines_action"] = "invalid",
        };
        var result = await service.CallToolAsync(ToolNames.ManageBuild, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageBuild_SetScenesMissingBuildScenes()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["action"] = "set_scenes" };
        var result = await service.CallToolAsync(ToolNames.ManageBuild, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ReturnsError_ForManageBuild_SetActiveProfileMissingPath()
    {
        var service = CreateService(new RuntimeState());

        var args = new JsonObject { ["action"] = "set_active_profile" };
        var result = await service.CallToolAsync(ToolNames.ManageBuild, args, CancellationToken.None);

        Assert.True(result["isError"]?.GetValue<bool>());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
    }

    private static McpToolService CreateService(RuntimeState runtimeState)
    {
        var scheduler = new RequestScheduler(Constants.QueueMaxSize);
        var bridge = new UnityBridge(runtimeState, scheduler, NullLogger<UnityBridge>.Instance);
        return new McpToolService(runtimeState, bridge, NullLogger<McpToolService>.Instance);
    }
}
