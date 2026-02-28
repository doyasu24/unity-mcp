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
        [ToolNames.GetPlayModeState] = new(
            ToolNames.GetPlayModeState,
            "sync",
            false,
            5000,
            10000,
            false,
            true,
            "Read-only: gets current Unity Editor play mode state.",
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
        [ToolNames.ClearConsole] = new(
            ToolNames.ClearConsole,
            "sync",
            false,
            5000,
            10000,
            false,
            false,
            "Clears Unity Console log entries.",
            EmptyObjectSchema()),
        [ToolNames.RefreshAssets] = new(
            ToolNames.RefreshAssets,
            "sync",
            false,
            30000,
            120000,
            false,
            false,
            "Refreshes Unity Editor assets.",
            EmptyObjectSchema()),
        [ToolNames.ControlPlayMode] = new(
            ToolNames.ControlPlayMode,
            "sync",
            false,
            10000,
            30000,
            false,
            false,
            "Edit: controls Unity Editor play mode (start, stop, pause).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = PlayModeActions.ToJsonArray(),
                    },
                },
                ["required"] = new JsonArray("action"),
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
        [ToolNames.GetSceneHierarchy] = new(
            ToolNames.GetSceneHierarchy,
            "sync",
            false,
            10000,
            30000,
            false,
            true,
            "Returns the scene's GameObject tree with component type names.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["root_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional: hierarchy path of a root GameObject to start from. If omitted, returns the entire scene.",
                    },
                    ["max_depth"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = SceneToolLimits.MaxDepthMin,
                        ["maximum"] = SceneToolLimits.MaxDepthMax,
                        ["default"] = SceneToolLimits.MaxDepthDefault,
                        ["description"] = "Maximum depth of the hierarchy tree to traverse. 0 returns only the root level.",
                    },
                    ["max_game_objects"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = SceneToolLimits.MaxGameObjectsMin,
                        ["maximum"] = SceneToolLimits.MaxGameObjectsMax,
                        ["default"] = SceneToolLimits.MaxGameObjectsDefault,
                        ["description"] = "Maximum number of GameObjects to include in the response. When exceeded, the response is truncated and 'truncated' is set to true. Use 'root_path' to drill into a specific subtree.",
                    },
                },
                ["additionalProperties"] = false,
            }),
        [ToolNames.GetComponentInfo] = new(
            ToolNames.GetComponentInfo,
            "sync",
            false,
            10000,
            30000,
            false,
            true,
            "Returns serialized field values of a specific component.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["game_object_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Scene hierarchy path of the target GameObject (e.g. \"/Canvas/Panel\" or \"Main Camera\").",
                    },
                    ["index"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["description"] = "0-based index of the component on the GameObject. Corresponds to the index from get_scene_hierarchy output.",
                    },
                    ["fields"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Optional list of field names to return. When specified, only these fields are included in the response. When omitted, all serialized fields are returned.",
                    },
                    ["max_array_elements"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = SceneToolLimits.MaxArrayElementsMin,
                        ["maximum"] = SceneToolLimits.MaxArrayElementsMax,
                        ["default"] = SceneToolLimits.MaxArrayElementsDefault,
                        ["description"] = "Maximum number of array/List elements to expand per field. Elements beyond this limit are truncated. 0 returns element count only.",
                    },
                },
                ["required"] = new JsonArray("game_object_path", "index"),
                ["additionalProperties"] = false,
            }),
        [ToolNames.ManageComponent] = new(
            ToolNames.ManageComponent,
            "sync",
            false,
            10000,
            30000,
            false,
            false,
            "Adds, updates, removes, or reorders components.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = ManageActions.ToJsonArray(),
                        ["description"] = "Operation to perform. 'add': requires component_type. 'update': requires index and fields. 'remove': requires index. 'move': requires index and new_index.",
                    },
                    ["game_object_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Scene hierarchy path of the target GameObject (e.g. \"/Canvas/Panel/Button\" or \"Main Camera\"). Root objects can omit the leading slash.",
                    },
                    ["component_type"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Fully qualified or simple name of the component type to add (e.g. \"Rigidbody\", \"PlayerController\", \"UnityEngine.UI.Image\"). Required for 'add' action.",
                    },
                    ["index"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["description"] = "0-based component index on the GameObject (matches get_scene_hierarchy output). Required for 'update'/'remove'/'move' to identify the target component. Optional for 'add' to specify insertion position (must be >= 1 since index 0 is Transform; default: append to end).",
                    },
                    ["new_index"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["description"] = "Target position for 'move' action. Required for 'move' only.",
                    },
                    ["fields"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "Key-value map of serialized field names to values. Applicable to 'add' and 'update' actions.",
                        ["additionalProperties"] = true,
                    },
                },
                ["required"] = new JsonArray("action", "game_object_path"),
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
