using System.IO;
using System.Text.Json.Nodes;
using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class ToolResultFormatterTests
{
    [Fact]
    public void Success_WithTypedPayload_SerializesStructuredContent()
    {
        var payload = new JsonObject { ["summary"] = new JsonObject { ["total"] = 5, ["passed"] = 5 } };
        var response = ToolResultFormatter.Success(new RunTestsResult(payload).Payload);

        var structured = Assert.IsType<JsonObject>(response["structuredContent"]);
        Assert.Equal(5, structured["summary"]?["total"]?.GetValue<int>());
        Assert.Equal(5, structured["summary"]?["passed"]?.GetValue<int>());

        var content = Assert.IsType<JsonArray>(response["content"]);
        var textItem = Assert.IsType<JsonObject>(content[0]);
        var text = textItem["text"]?.GetValue<string>();

        Assert.Contains("\"total\":5", text);
        Assert.Contains("\"passed\":5", text);
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
        Assert.False(structured["retryable"]?.GetValue<bool>());
        Assert.Equal("mode", structured["details"]?["field"]?.GetValue<string>());
        Assert.Equal(ExecutionGuarantees.Unknown, structured["details"]?["execution_guarantee"]?.GetValue<string>());
        Assert.Equal(RecoveryActions.InspectStateThenRetryIfNeeded, structured["details"]?["recovery_action"]?.GetValue<string>());
    }

    [Fact]
    public void Error_MapsEditorNotReadyToRetryableNotExecuted()
    {
        var error = new McpException(ErrorCodes.EditorNotReady, "not ready");

        var response = ToolResultFormatter.Error(error);

        var structured = Assert.IsType<JsonObject>(response["structuredContent"]);
        Assert.True(structured["retryable"]?.GetValue<bool>());
        Assert.Equal(ExecutionGuarantees.NotExecuted, structured["details"]?["execution_guarantee"]?.GetValue<string>());
        Assert.Equal(RecoveryActions.RetryAllowed, structured["details"]?["recovery_action"]?.GetValue<string>());
    }

    [Fact]
    public void SuccessWithImage_IncludesImageAndTextContent()
    {
        var metadata = new JsonObject { ["file_path"] = "/test.png", ["width"] = 100 };
        var base64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var response = ToolResultFormatter.SuccessWithImage(metadata, base64, "image/png");

        var content = Assert.IsType<JsonArray>(response["content"]);
        Assert.Equal(2, content.Count);

        var imageBlock = Assert.IsType<JsonObject>(content[0]);
        Assert.Equal("image", imageBlock["type"]?.GetValue<string>());
        Assert.Equal(base64, imageBlock["data"]?.GetValue<string>());
        Assert.Equal("image/png", imageBlock["mimeType"]?.GetValue<string>());

        var textBlock = Assert.IsType<JsonObject>(content[1]);
        Assert.Equal("text", textBlock["type"]?.GetValue<string>());

        Assert.Null(response["structuredContent"]);
        Assert.Null(response["isError"]);
    }

    [Fact]
    public void FormatScreenshotResult_WithInlineImage_ReturnsImageAndText()
    {
        // Plugin 側で生成された inline_image base64 を使用するフォーマット
        var inlineBase64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var payload = new JsonObject
        {
            ["file_path"] = "/path/to/test.png",
            ["width"] = 1920,
            ["height"] = 1080,
            ["camera_name"] = "Main Camera",
            ["source"] = "game_view",
            ["inline_image"] = inlineBase64,
        };

        var result = McpToolService.FormatScreenshotResult(payload);

        var content = Assert.IsType<JsonArray>(result["content"]);
        Assert.Equal(2, content.Count);

        var imageBlock = Assert.IsType<JsonObject>(content[0]);
        Assert.Equal("image", imageBlock["type"]?.GetValue<string>());
        Assert.Equal(inlineBase64, imageBlock["data"]?.GetValue<string>());

        var textBlock = Assert.IsType<JsonObject>(content[1]);
        Assert.Equal("text", textBlock["type"]?.GetValue<string>());
        // inline_image はテキストメタデータから除去されていること
        var textJson = textBlock["text"]?.GetValue<string>();
        Assert.DoesNotContain("inline_image", textJson);

        Assert.Null(result["structuredContent"]);
    }

    [Fact]
    public void FormatScreenshotResult_WithoutInlineImage_ReturnsTextOnly()
    {
        var payload = new JsonObject
        {
            ["file_path"] = "/path/to/test.png",
            ["width"] = 1920,
            ["height"] = 1080,
            ["camera_name"] = "Main Camera",
            ["source"] = "game_view",
        };

        var result = McpToolService.FormatScreenshotResult(payload);

        // inline_image がない場合はテキストのみ（structuredContent 形式）
        Assert.NotNull(result["structuredContent"]);
    }
}
