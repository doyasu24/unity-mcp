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
    public void BuildMcpTools_ContainsSceneAndComponentTools()
    {
        var tools = ToolCatalog.BuildMcpTools();

        AssertToolExists(tools, ToolNames.GetSceneHierarchy);
        AssertToolExists(tools, ToolNames.GetSceneComponentInfo);
        AssertToolExists(tools, ToolNames.ManageSceneComponent);
    }

    [Fact]
    public void BuildUnityCapabilityTools_ContainsSceneAndComponentTools()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();

        AssertSyncToolWithoutCancel(tools, ToolNames.GetSceneHierarchy);
        AssertSyncToolWithoutCancel(tools, ToolNames.GetSceneComponentInfo);
        AssertSyncToolWithoutCancel(tools, ToolNames.ManageSceneComponent);
    }

    [Fact]
    public void BuildMcpTools_ManageComponentSchema_HasActionEnum()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var manageComponent = AssertToolExists(tools, ToolNames.ManageSceneComponent);
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
        var getComponentInfo = AssertToolExists(tools, ToolNames.GetSceneComponentInfo);
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
    public void BuildMcpTools_ContainsPrefabTools()
    {
        var tools = ToolCatalog.BuildMcpTools();

        AssertToolExists(tools, ToolNames.GetPrefabHierarchy);
        AssertToolExists(tools, ToolNames.GetPrefabComponentInfo);
        AssertToolExists(tools, ToolNames.ManagePrefabComponent);
    }

    [Fact]
    public void BuildUnityCapabilityTools_ContainsPrefabTools()
    {
        var tools = ToolCatalog.BuildUnityCapabilityTools();

        AssertSyncToolWithoutCancel(tools, ToolNames.GetPrefabHierarchy);
        AssertSyncToolWithoutCancel(tools, ToolNames.GetPrefabComponentInfo);
        AssertSyncToolWithoutCancel(tools, ToolNames.ManagePrefabComponent);
    }

    [Fact]
    public void BuildMcpTools_GetPrefabHierarchySchema_RequiresPrefabPath()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.GetPrefabHierarchy);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var required = Assert.IsType<JsonArray>(schema["required"]);

        var requiredNames = required.Select(n => n?.GetValue<string>()).ToList();
        Assert.Contains("prefab_path", requiredNames);
    }

    [Fact]
    public void BuildMcpTools_GetPrefabComponentInfoSchema_RequiresPrefabPathGameObjectPathAndIndex()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.GetPrefabComponentInfo);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var required = Assert.IsType<JsonArray>(schema["required"]);

        var requiredNames = required.Select(n => n?.GetValue<string>()).ToList();
        Assert.Contains("prefab_path", requiredNames);
        Assert.Contains("game_object_path", requiredNames);
        Assert.Contains("index", requiredNames);
    }

    [Fact]
    public void BuildMcpTools_ManagePrefabComponentSchema_RequiresPrefabPathActionGameObjectPath()
    {
        var tools = ToolCatalog.BuildMcpTools();
        var tool = AssertToolExists(tools, ToolNames.ManagePrefabComponent);
        var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        var required = Assert.IsType<JsonArray>(schema["required"]);

        var requiredNames = required.Select(n => n?.GetValue<string>()).ToList();
        Assert.Contains("prefab_path", requiredNames);
        Assert.Contains("action", requiredNames);
        Assert.Contains("game_object_path", requiredNames);
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
