using System.Text.Json.Nodes;
using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class ToolCatalogTests
{
    [Fact]
    public void BuildMcpTools_ContainsClearConsoleAndRefreshAssets()
    {
        var tools = ToolCatalog.BuildMcpTools();

        AssertToolExists(tools, ToolNames.ClearConsole);
        AssertToolExists(tools, ToolNames.RefreshAssets);
    }

    [Fact]
    public void BuildUnityCapabilityTools_MarksClearAndRefreshAsSyncWithoutCancel()
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
