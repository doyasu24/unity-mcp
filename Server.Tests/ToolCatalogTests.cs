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
        Assert.Equal("sync", clearConsole["execution_mode"]?.GetValue<string>());
        Assert.False(clearConsole["supports_cancel"]?.GetValue<bool>());
        Assert.False(clearConsole["requires_client_request_id"]?.GetValue<bool>());

        var refreshAssets = AssertToolExists(tools, ToolNames.RefreshAssets);
        Assert.Equal("sync", refreshAssets["execution_mode"]?.GetValue<string>());
        Assert.False(refreshAssets["supports_cancel"]?.GetValue<bool>());
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
    public void BuildMcpTools_GetComponentInfoSchema_RequiresGameObjectPathAndIndex()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var getComponentInfo = AssertToolExists(tools, ToolNames.GetComponentInfo);
        var schema = Assert.IsType<JsonObject>(getComponentInfo["inputSchema"]);
        var required = Assert.IsType<JsonArray>(schema["required"]);

        var requiredNames = required.Select(n => n?.GetValue<string>()).ToList();
        Assert.Contains("game_object_path", requiredNames);
        Assert.Contains("index", requiredNames);
    }

    [Fact]
    public void BuildUnityCapabilityTools_GetSceneHierarchy_IsRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.GetSceneHierarchy);
        Assert.True(tool["execution_error_retryable"]?.GetValue<bool>());
    }

    [Fact]
    public void BuildUnityCapabilityTools_ManageComponent_IsNotRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.ManageSceneComponent);
        Assert.False(tool["execution_error_retryable"]?.GetValue<bool>());
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
    public void BuildUnityCapabilityTools_GetPrefabHierarchy_IsRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.GetPrefabHierarchy);
        Assert.True(tool["execution_error_retryable"]?.GetValue<bool>());
    }

    [Fact]
    public void BuildUnityCapabilityTools_ManagePrefabComponent_IsNotRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.ManagePrefabComponent);
        Assert.False(tool["execution_error_retryable"]?.GetValue<bool>());
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
    public void BuildUnityCapabilityTools_ListScenes_IsRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.ListScenes);
        Assert.True(tool["execution_error_retryable"]?.GetValue<bool>());
    }

    [Fact]
    public void BuildUnityCapabilityTools_FindAssets_IsRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.FindAssets);
        Assert.True(tool["execution_error_retryable"]?.GetValue<bool>());
    }

    [Fact]
    public void BuildUnityCapabilityTools_FindSceneGameObjects_IsRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.FindSceneGameObjects);
        Assert.True(tool["execution_error_retryable"]?.GetValue<bool>());
    }

    [Fact]
    public void BuildUnityCapabilityTools_OpenScene_IsNotRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.OpenScene);
        Assert.False(tool["execution_error_retryable"]?.GetValue<bool>());
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
    public void BuildUnityCapabilityTools_InstantiatePrefab_IsNotRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.InstantiatePrefab);
        Assert.False(tool["execution_error_retryable"]?.GetValue<bool>());
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
    public void BuildUnityCapabilityTools_GetAssetInfo_IsRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.GetAssetInfo);
        Assert.True(tool["execution_error_retryable"]?.GetValue<bool>());
    }

    [Fact]
    public void BuildUnityCapabilityTools_FindPrefabGameObjects_IsRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.FindPrefabGameObjects);
        Assert.True(tool["execution_error_retryable"]?.GetValue<bool>());
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
    }

    [Fact]
    public void BuildUnityCapabilityTools_ManageAsset_IsNotRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.ManageAsset);
        Assert.False(tool["execution_error_retryable"]?.GetValue<bool>());
    }

    [Fact]
    public void BuildUnityCapabilityTools_CaptureScreenshot_IsRetryable()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();
        var tool = AssertToolExists(tools, ToolNames.CaptureScreenshot);
        Assert.True(tool["execution_error_retryable"]?.GetValue<bool>());
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

    private static void AssertSyncToolWithoutCancel(JsonArray tools, string toolName)
    {
        var tool = AssertToolExists(tools, toolName);
        Assert.Equal("sync", tool["execution_mode"]?.GetValue<string>());
        Assert.False(tool["supports_cancel"]?.GetValue<bool>());
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
