using System.Text.Json.Nodes;

namespace UnityMcpServer;

internal sealed record ToolMetadata(
    string Name,
    string ExecutionMode,
    bool SupportsCancel,
    int DefaultTimeoutMs,
    int MaxTimeoutMs,
    bool RequiresClientRequestId,
    bool ExecutionErrorRetryable,
    string Description,
    JsonObject InputSchema);

internal static class ToolCatalog
{
    public static readonly IReadOnlyDictionary<string, ToolMetadata> Items = new Dictionary<string, ToolMetadata>(StringComparer.Ordinal)
    {
        [ToolNames.GetEditorState] = new(
            ToolNames.GetEditorState,
            "sync",
            false,
            5000,
            10000,
            false,
            true,
            "Returns current server/editor connection state.",
            EmptyObjectSchema()),
        [ToolNames.ReadConsole] = new(
            ToolNames.ReadConsole,
            "sync",
            false,
            10000,
            30000,
            false,
            true,
            "Reads Unity console entries.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["max_entries"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = ToolLimits.ReadConsoleMinEntries,
                        ["maximum"] = ToolLimits.ReadConsoleMaxEntries,
                        ["default"] = ToolLimits.ReadConsoleDefaultMaxEntries,
                    },
                },
                ["additionalProperties"] = false,
            }),
        [ToolNames.RunTests] = new(
            ToolNames.RunTests,
            "job",
            true,
            300000,
            1800000,
            false,
            false,
            "Starts Unity tests as a cancellable job.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["mode"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = RunTestsModes.ToJsonArray(),
                        ["default"] = RunTestsModes.All,
                    },
                    ["filter"] = new JsonObject
                    {
                        ["type"] = "string",
                    },
                },
                ["additionalProperties"] = false,
            }),
        [ToolNames.GetJobStatus] = new(
            ToolNames.GetJobStatus,
            "sync",
            false,
            5000,
            10000,
            false,
            false,
            "Checks state/result of a submitted test job.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["job_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["minLength"] = 1,
                    },
                },
                ["required"] = new JsonArray("job_id"),
                ["additionalProperties"] = false,
            }),
        [ToolNames.CancelJob] = new(
            ToolNames.CancelJob,
            "sync",
            false,
            5000,
            10000,
            false,
            false,
            "Requests cancellation for a running/queued job.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["job_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["minLength"] = 1,
                    },
                },
                ["required"] = new JsonArray("job_id"),
                ["additionalProperties"] = false,
            }),
    };

    public static JsonArray BuildMcpTools()
    {
        var tools = new JsonArray();
        foreach (var tool in Items.Values)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }

        return tools;
    }

    public static JsonArray BuildUnityCapabilityTools()
    {
        var tools = new JsonArray();
        foreach (var tool in Items.Values)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["execution_mode"] = tool.ExecutionMode,
                ["supports_cancel"] = tool.SupportsCancel,
                ["default_timeout_ms"] = tool.DefaultTimeoutMs,
                ["max_timeout_ms"] = tool.MaxTimeoutMs,
                ["requires_client_request_id"] = tool.RequiresClientRequestId,
                ["execution_error_retryable"] = tool.ExecutionErrorRetryable,
            });
        }

        return tools;
    }

    public static int DefaultTimeoutMs(string toolName)
    {
        if (!Items.TryGetValue(toolName, out var metadata))
        {
            throw new McpException(ErrorCodes.UnknownCommand, $"Unknown tool: {toolName}");
        }

        return metadata.DefaultTimeoutMs;
    }

    private static JsonObject EmptyObjectSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["additionalProperties"] = false,
        };
    }
}
