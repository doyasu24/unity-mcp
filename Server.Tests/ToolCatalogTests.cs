using System.Text.Json.Nodes;
using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class ToolCatalogTests
{
    [Fact]
    public void BuildMcpTools_ContainsEditorControlTools()
    {
        var tools = ToolCatalog.BuildMcpTools();

        AssertToolExists(tools, ToolNames.ClearConsole);
        AssertToolExists(tools, ToolNames.RefreshAssets);
        AssertToolExists(tools, ToolNames.GetPlayModeState);
        AssertToolExists(tools, ToolNames.ControlPlayMode);
    }

    [Fact]
    public void BuildUnityCapabilityTools_MarksEditorControlToolsAsSyncWithoutCancel()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();

        var clearConsole = AssertToolExists(tools, ToolNames.ClearConsole);
        Assert.False(clearConsole["requires_client_request_id"]?.GetValue<bool>());

        var refreshAssets = AssertToolExists(tools, ToolNames.RefreshAssets);
        Assert.False(refreshAssets["requires_client_request_id"]?.GetValue<bool>());

        AssertSyncToolWithoutCancel(tools, ToolNames.GetPlayModeState);
        AssertSyncToolWithoutCancel(tools, ToolNames.ControlPlayMode);
    }

    [Fact]
    public void BuildMcpTools_ControlPlayModeSchema_RequiresActionEnum()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var controlPlayMode = AssertToolExists(tools, ToolNames.ControlPlayMode);
        var schema = Assert.IsType<JsonObject>(controlPlayMode["inputSchema"]);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        var action = Assert.IsType<JsonObject>(properties["action"]);
        var @enum = Assert.IsType<JsonArray>(action["enum"]);

        Assert.Contains(PlayModeActions.Start, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(PlayModeActions.Stop, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(PlayModeActions.Pause, @enum.Select(node => node?.GetValue<string>()));
    }

    [Fact]
    public void BuildMcpTools_GetPlayModeStateSchema_IsEmptyObject()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var getPlayModeState = AssertToolExists(tools, ToolNames.GetPlayModeState);
        var schema = Assert.IsType<JsonObject>(getPlayModeState["inputSchema"]);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);

        Assert.Empty(properties);
        Assert.False(schema["additionalProperties"]?.GetValue<bool>());
    }

    [Fact]
    public void BuildMcpTools_ContainsUnifiedScenePrefabTools()
    {
        var tools = ToolCatalog.BuildMcpTools();

        AssertToolExists(tools, ToolNames.GetHierarchy);
        AssertToolExists(tools, ToolNames.GetComponentInfo);
        AssertToolExists(tools, ToolNames.ManageComponent);
        AssertToolExists(tools, ToolNames.FindGameObjects);
        AssertToolExists(tools, ToolNames.ManageGameObject);
    }

    [Fact]
    public void BuildUnityCapabilityTools_ExpandsUnifiedToolsToWireNames()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();

        AssertSyncToolWithoutCancel(tools, ToolNames.GetSceneHierarchy);
        AssertSyncToolWithoutCancel(tools, ToolNames.GetSceneComponentInfo);
        AssertSyncToolWithoutCancel(tools, ToolNames.ManageSceneComponent);
        AssertSyncToolWithoutCancel(tools, ToolNames.GetPrefabHierarchy);
        AssertSyncToolWithoutCancel(tools, ToolNames.GetPrefabComponentInfo);
        AssertSyncToolWithoutCancel(tools, ToolNames.ManagePrefabComponent);
        AssertSyncToolWithoutCancel(tools, ToolNames.FindSceneGameObjects);
        AssertSyncToolWithoutCancel(tools, ToolNames.FindPrefabGameObjects);
        AssertSyncToolWithoutCancel(tools, ToolNames.ManageSceneGameObject);
        AssertSyncToolWithoutCancel(tools, ToolNames.ManagePrefabGameObject);
    }

    [Fact]
    public void BuildMcpTools_ManageComponentSchema_HasActionEnum()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var manageComponent = AssertToolExists(tools, ToolNames.ManageComponent);
        var schema = Assert.IsType<JsonObject>(manageComponent["inputSchema"]);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        var action = Assert.IsType<JsonObject>(properties["action"]);
        var @enum = Assert.IsType<JsonArray>(action["enum"]);

        Assert.Contains(ManageActions.Add, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(ManageActions.Update, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(ManageActions.Remove, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(ManageActions.Move, @enum.Select(node => node?.GetValue<string>()));
    }

    [Fact]
    public void BuildMcpTools_GetComponentInfoSchema_RequiresGameObjectPath_IndexOptional()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var getComponentInfo = AssertToolExists(tools, ToolNames.GetComponentInfo);
        var schema = Assert.IsType<JsonObject>(getComponentInfo["inputSchema"]);
        var required = Assert.IsType<JsonArray>(schema["required"]);

        var requiredNames = required.Select(n => n?.GetValue<string>()).ToList();
        Assert.Contains("game_object_path", requiredNames);
        // index は省略可能（省略時はコンポーネント一覧モード）
        Assert.DoesNotContain("index", requiredNames);
    }

    [Fact]
    public void BuildMcpTools_UnifiedToolsHaveOptionalPrefabPath()
    {
        var tools = ToolCatalog.BuildMcpTools();

        foreach (var name in new[] { ToolNames.GetHierarchy, ToolNames.GetComponentInfo, ToolNames.ManageComponent, ToolNames.FindGameObjects, ToolNames.ManageGameObject })
        {
            var tool = AssertToolExists(tools, name);
            var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
            var properties = Assert.IsType<JsonObject>(schema["properties"]);
            Assert.Contains("prefab_path", properties.Select(p => p.Key));

            var required = schema["required"] as JsonArray;
            if (required is not null)
            {
                Assert.DoesNotContain("prefab_path", required.Select(n => n?.GetValue<string>()));
            }
        }
    }

    [Fact]
    public void DefaultTimeoutMs_FallsBackToWireAlias()
    {
        var timeout = ToolCatalog.DefaultTimeoutMs(ToolNames.GetSceneHierarchy);
        Assert.Equal(ToolCatalog.DefaultTimeoutMs(ToolNames.GetHierarchy), timeout);
    }

    [Fact]
    public void BuildMcpTools_ContainsSceneManagementTools()
    {
        var tools = ToolCatalog.BuildMcpTools();

        AssertToolExists(tools, ToolNames.ListScenes);
        AssertToolExists(tools, ToolNames.OpenScene);
        AssertToolExists(tools, ToolNames.SaveScene);
        AssertToolExists(tools, ToolNames.CreateScene);
    }

    [Fact]
    public void BuildMcpTools_ContainsAssetSearchTools()
    {
        var tools = ToolCatalog.BuildMcpTools();

        AssertToolExists(tools, ToolNames.FindAssets);
        AssertToolExists(tools, ToolNames.FindGameObjects);
    }

    [Fact]
    public void BuildUnityCapabilityTools_ContainsSceneManagementTools()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();

        AssertSyncToolWithoutCancel(tools, ToolNames.ListScenes);
        AssertSyncToolWithoutCancel(tools, ToolNames.OpenScene);
        AssertSyncToolWithoutCancel(tools, ToolNames.SaveScene);
        AssertSyncToolWithoutCancel(tools, ToolNames.CreateScene);
    }

    [Fact]
    public void BuildUnityCapabilityTools_ContainsAssetSearchTools()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();

        AssertSyncToolWithoutCancel(tools, ToolNames.FindAssets);
        AssertSyncToolWithoutCancel(tools, ToolNames.FindSceneGameObjects);
        AssertSyncToolWithoutCancel(tools, ToolNames.FindPrefabGameObjects);
    }

    [Fact]
    public void BuildMcpTools_OpenSceneSchema_RequiresPath()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.OpenScene);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var required = Assert.IsType<JsonArray>(schema["required"]);

        var requiredNames = required.Select(n => n?.GetValue<string>()).ToList();
        Assert.Contains("path", requiredNames);
    }

    [Fact]
    public void BuildMcpTools_OpenSceneSchema_HasModeEnum()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.OpenScene);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        var mode = Assert.IsType<JsonObject>(properties["mode"]);
        var @enum = Assert.IsType<JsonArray>(mode["enum"]);

        Assert.Contains(OpenSceneModes.Single, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(OpenSceneModes.Additive, @enum.Select(node => node?.GetValue<string>()));
    }

    [Fact]
    public void BuildMcpTools_CreateSceneSchema_RequiresPath()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.CreateScene);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var required = Assert.IsType<JsonArray>(schema["required"]);

        var requiredNames = required.Select(n => n?.GetValue<string>()).ToList();
        Assert.Contains("path", requiredNames);
    }

    [Fact]
    public void BuildMcpTools_FindAssetsSchema_RequiresFilter()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.FindAssets);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var required = Assert.IsType<JsonArray>(schema["required"]);

        var requiredNames = required.Select(n => n?.GetValue<string>()).ToList();
        Assert.Contains("filter", requiredNames);
    }

    [Fact]
    public void BuildMcpTools_FindAssetsSchema_HasMaxResultsWithLimits()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.FindAssets);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        var maxResults = Assert.IsType<JsonObject>(properties["max_results"]);

        Assert.Equal("integer", maxResults["type"]?.GetValue<string>());
        Assert.Equal(FindAssetsLimits.MaxResultsMin, maxResults["minimum"]?.GetValue<int>());
        Assert.Equal(FindAssetsLimits.MaxResultsMax, maxResults["maximum"]?.GetValue<int>());
        Assert.Equal(FindAssetsLimits.MaxResultsDefault, maxResults["default"]?.GetValue<int>());
    }

    [Fact]
    public void BuildMcpTools_FindAssetsSchema_HasSearchInFoldersArray()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.FindAssets);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        var searchInFolders = Assert.IsType<JsonObject>(properties["search_in_folders"]);

        Assert.Equal("array", searchInFolders["type"]?.GetValue<string>());
        var items = Assert.IsType<JsonObject>(searchInFolders["items"]);
        Assert.Equal("string", items["type"]?.GetValue<string>());
    }

    [Fact]
    public void BuildMcpTools_ListScenesSchema_HasFilteringAndPagination()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.ListScenes);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);

        Assert.NotNull(properties["name_pattern"]);
        Assert.Equal("string", properties["name_pattern"]!["type"]?.GetValue<string>());
        Assert.NotNull(properties["max_results"]);
        Assert.Equal("integer", properties["max_results"]!["type"]?.GetValue<string>());
        Assert.NotNull(properties["offset"]);
        Assert.Equal("integer", properties["offset"]!["type"]?.GetValue<string>());
        Assert.False(schema["additionalProperties"]?.GetValue<bool>());
    }

    [Fact]
    public void BuildMcpTools_GetHierarchySchema_HasComponentFilter()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.GetHierarchy);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);

        var componentFilter = Assert.IsType<JsonObject>(properties["component_filter"]);
        Assert.Equal("array", componentFilter["type"]?.GetValue<string>());
        var items = Assert.IsType<JsonObject>(componentFilter["items"]);
        Assert.Equal("string", items["type"]?.GetValue<string>());
    }

    [Fact]
    public void BuildMcpTools_ContainsInstantiatePrefab()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.InstantiatePrefab);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var required = Assert.IsType<JsonArray>(schema["required"]);
        var requiredNames = required.Select(n => n?.GetValue<string>()).ToList();
        Assert.Contains("prefab_path", requiredNames);
    }

    [Fact]
    public void BuildMcpTools_ContainsGetAssetInfo()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.GetAssetInfo);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var required = Assert.IsType<JsonArray>(schema["required"]);
        var requiredNames = required.Select(n => n?.GetValue<string>()).ToList();
        Assert.Contains("asset_path", requiredNames);
    }

    [Fact]
    public void BuildMcpTools_ContainsManageAssetAndCaptureScreenshot()
    {
        var tools = ToolCatalog.BuildMcpTools();

        AssertToolExists(tools, ToolNames.ManageAsset);
        AssertToolExists(tools, ToolNames.CaptureScreenshot);
    }

    [Fact]
    public void BuildMcpTools_ManageAssetSchema_HasActionEnum()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.ManageAsset);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        var action = Assert.IsType<JsonObject>(properties["action"]);
        var @enum = Assert.IsType<JsonArray>(action["enum"]);

        Assert.Contains(ManageAssetActions.Create, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(ManageAssetActions.Delete, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(ManageAssetActions.GetProperties, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(ManageAssetActions.SetProperties, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(ManageAssetActions.SetShader, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(ManageAssetActions.GetKeywords, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(ManageAssetActions.SetKeywords, @enum.Select(node => node?.GetValue<string>()));
    }

    [Fact]
    public void BuildMcpTools_CaptureScreenshotSchema_HasSourceEnum()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.CaptureScreenshot);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        var source = Assert.IsType<JsonObject>(properties["source"]);
        var @enum = Assert.IsType<JsonArray>(source["enum"]);

        Assert.Contains(ScreenshotSources.GameView, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(ScreenshotSources.SceneView, @enum.Select(node => node?.GetValue<string>()));
        Assert.Contains(ScreenshotSources.Camera, @enum.Select(node => node?.GetValue<string>()));
    }

    [Fact]
    public void BuildMcpTools_GetPlayModeStateDescription_NoPrefix()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.GetPlayModeState);
        var description = tool["description"]?.GetValue<string>();
        Assert.NotNull(description);
        Assert.DoesNotContain("Read-only:", description);
        Assert.StartsWith("Gets current", description);
    }

    [Fact]
    public void BuildMcpTools_ControlPlayModeDescription_NoPrefix()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.ControlPlayMode);
        var description = tool["description"]?.GetValue<string>();
        Assert.NotNull(description);
        Assert.DoesNotContain("Edit:", description);
        Assert.StartsWith("Controls Unity", description);
    }

    [Fact]
    public void BuildMcpTools_RefreshAssetsDescription_MentionsAutoStopPlayMode()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.RefreshAssets);
        var description = tool["description"]?.GetValue<string>();
        Assert.NotNull(description);
        Assert.Contains("Automatically stops play mode", description);
    }

    [Fact]
    public void BuildMcpTools_ContainsExecuteBatch()
    {
        var tools = ToolCatalog.BuildMcpTools();
        AssertToolExists(tools, ToolNames.ExecuteBatch);
    }

    [Fact]
    public void BuildMcpTools_ExecuteBatchSchema_RequiresOperations()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.ExecuteBatch);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var required = Assert.IsType<JsonArray>(schema["required"]);

        var requiredNames = required.Select(n => n?.GetValue<string>()).ToList();
        Assert.Contains("operations", requiredNames);
    }

    [Fact]
    public void DefaultTimeoutMs_ExecuteBatch_Returns600000()
    {
        Assert.Equal(600000, ToolCatalog.DefaultTimeoutMs(ToolNames.ExecuteBatch));
    }

    [Fact]
    public void MaxTimeoutMs_ExecuteBatch_Returns2400000()
    {
        Assert.True(ToolCatalog.Items.TryGetValue(ToolNames.ExecuteBatch, out var metadata));
        Assert.Equal(2400000, metadata.MaxTimeoutMs);
    }

    [Fact]
    public void BuildMcpTools_RunTestsSchema_HasTestFullNameAndTestNamePattern()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.RunTests);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);

        Assert.NotNull(properties["test_full_name"]);
        Assert.Equal("string", properties["test_full_name"]!["type"]?.GetValue<string>());

        Assert.NotNull(properties["test_name_pattern"]);
        Assert.Equal("string", properties["test_name_pattern"]!["type"]?.GetValue<string>());

        // Old 'filter' property must not exist
        Assert.False(properties.ContainsKey("filter"));
    }

    private static void AssertSyncToolWithoutCancel(JsonArray tools, string toolName)
    {
        var tool = AssertToolExists(tools, toolName);
        Assert.False(tool["requires_client_request_id"]?.GetValue<bool>());
    }

    private static JsonObject AssertToolExists(JsonArray tools, string toolName)
    {
        foreach (var node in tools)
        {
            if (node is not JsonObject tool)
            {
                continue;
            }

            if (string.Equals(tool["name"]?.GetValue<string>(), toolName, StringComparison.Ordinal))
            {
                return tool;
            }
        }

        throw new Xunit.Sdk.XunitException($"Tool '{toolName}' was not found.");
    }
}
