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
