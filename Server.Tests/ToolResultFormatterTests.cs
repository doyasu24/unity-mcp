using System.Text.Json.Nodes;
using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class ToolResultFormatterTests
{
    [Fact]
    public void Success_WithTypedPayload_SerializesStructuredContent()
    {
        var response = ToolResultFormatter.Success(new RunTestsResult("job-1", "queued"));

        var structured = Assert.IsType<JsonObject>(response["structuredContent"]);
        Assert.Equal("job-1", structured["jobId"]?.GetValue<string>());
        Assert.Equal("queued", structured["state"]?.GetValue<string>());

        var content = Assert.IsType<JsonArray>(response["content"]);
        var textItem = Assert.IsType<JsonObject>(content[0]);
        var text = textItem["text"]?.GetValue<string>();

        Assert.Contains("\"jobId\":\"job-1\"", text);
        Assert.Contains("\"state\":\"queued\"", text);
    }

    [Fact]
    public void Success_WithJsonNode_DeepClonesStructuredContent()
    {
        var source = new JsonObject { ["value"] = 1 };

        var response = ToolResultFormatter.Success(source);
        source["value"] = 2;

        var structured = Assert.IsType<JsonObject>(response["structuredContent"]);
        Assert.Equal(1, structured["value"]?.GetValue<int>());
    }

    [Fact]
    public void Error_IncludesCodeMessageAndDetails()
    {
        var details = new JsonObject { ["field"] = "mode" };
        var error = new McpException(ErrorCodes.InvalidParams, "invalid", details);

        var response = ToolResultFormatter.Error(error);

        Assert.True(response["isError"]?.GetValue<bool>());

        var structured = Assert.IsType<JsonObject>(response["structuredContent"]);
        Assert.Equal(ErrorCodes.InvalidParams, structured["code"]?.GetValue<string>());
        Assert.Equal("invalid", structured["message"]?.GetValue<string>());
        Assert.Equal("mode", structured["details"]?["field"]?.GetValue<string>());
    }
}
