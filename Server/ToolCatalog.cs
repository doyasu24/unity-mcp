using System.Text.Json.Nodes;

namespace UnityMcpServer;

internal sealed record ToolMetadata(
    string Name,
    int DefaultTimeoutMs,
    int MaxTimeoutMs,
    bool RequiresClientRequestId,
    string Description,
    JsonObject InputSchema);

internal static class ToolCatalog
{
    public static readonly IReadOnlyDictionary<string, ToolMetadata> Items = new Dictionary<string, ToolMetadata>(StringComparer.Ordinal)
    {
        [ToolNames.GetEditorState] = new(
            ToolNames.GetEditorState,
            5000,
            10000,
            false,
            "Returns current server/editor connection state.",
            EmptyObjectSchema()),
        [ToolNames.GetPlayModeState] = new(
            ToolNames.GetPlayModeState,
            5000,
            10000,
            false,
            "Gets current Unity Editor play mode state.",
            EmptyObjectSchema()),
        [ToolNames.ReadConsole] = new(
            ToolNames.ReadConsole,
            10000,
            30000,
            false,
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
                        ["description"] = "Maximum number of entries to return. 0 returns counts only.",
                    },
                    ["log_type"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = ConsoleLogTypes.ToJsonArray(),
                        },
                        ["description"] = "Filter by log type. Omit to include all types.",
                    },
                    ["message_pattern"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Filter entries by message content (regex, case-insensitive).",
                    },
                    ["stack_trace_lines"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["default"] = ToolLimits.ReadConsoleDefaultStackTraceLines,
                        ["description"] = "Max stack trace lines per entry. 0 omits stack traces entirely.",
                    },
                    ["deduplicate"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["default"] = true,
                        ["description"] = "Collapse consecutive identical entries (same type+message) into one with a count field.",
                    },
                    ["offset"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["default"] = 0,
                        ["description"] = "Number of matching entries to skip. Use next_offset from truncated responses.",
                    },
                },
                ["additionalProperties"] = false,
            }),
        [ToolNames.ClearConsole] = new(
            ToolNames.ClearConsole,
            5000,
            10000,
            false,
            "Clears Unity Console log entries.",
            EmptyObjectSchema()),
        [ToolNames.RefreshAssets] = new(
            ToolNames.RefreshAssets,
            120000,
            300000,
            false,
            "Refreshes Unity Editor assets.",
            EmptyObjectSchema()),
        [ToolNames.ControlPlayMode] = new(
            ToolNames.ControlPlayMode,
            10000,
            30000,
            false,
            "Controls Unity Editor play mode (start, stop, pause).",
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
            300000,
            1800000,
            false,
            "Runs Unity tests and returns the result.",
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
        [ToolNames.GetHierarchy] = new(
            ToolNames.GetHierarchy,
            10000,
            30000,
            false,
            "Returns the scene's or a Prefab asset's GameObject tree with component type names.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["prefab_path"] = PrefabPathProperty("Omit for active scene."),
                    ["root_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional: hierarchy path of a root GameObject to start from. If omitted, returns the entire scene or Prefab root.",
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
                    ["offset"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["default"] = 0,
                        ["description"] = "Number of results to skip. Use 'next_offset' from a truncated response to fetch the next page.",
                    },
                    ["component_filter"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Only return GameObjects that have at least one of these component types. Matches Type.Name or Type.FullName (case-insensitive). In tree mode (offset=0), non-matching GOs keep structure but omit components. In flat mode (offset>0), non-matching GOs are excluded.",
                    },
                },
                ["additionalProperties"] = false,
            }),
        [ToolNames.GetComponentInfo] = new(
            ToolNames.GetComponentInfo,
            10000,
            30000,
            false,
            "Returns serialized field values of a specific component on a scene or Prefab GameObject.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["prefab_path"] = PrefabPathProperty("Omit for active scene."),
                    ["game_object_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Scene hierarchy path of the target GameObject (e.g. \"/Canvas/Panel\" or \"Main Camera\").",
                    },
                    ["index"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["description"] = "0-based index of the component on the GameObject. Corresponds to the index from get_hierarchy output.",
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
            10000,
            30000,
            false,
            "Adds, updates, removes, or reorders components on a scene or Prefab GameObject.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["prefab_path"] = PrefabPathProperty("Omit for active scene."),
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = ManageActions.ToJsonArray(),
                        ["description"] = "Operation to perform. 'add': requires component_type. 'update': requires index and fields. 'remove': requires index. 'move': requires index and new_index.",
                    },
                    ["game_object_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Hierarchy path of the target GameObject (e.g. \"/Canvas/Panel/Button\" or \"Main Camera\"). Root objects can omit the leading slash.",
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
                        ["description"] = "0-based component index on the GameObject (matches get_hierarchy output). Required for 'update'/'remove'/'move' to identify the target component. Optional for 'add' to specify insertion position (must be >= 1 since index 0 is Transform; default: append to end).",
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
        [ToolNames.ListScenes] = new(
            ToolNames.ListScenes,
            5000,
            10000,
            false,
            "Lists scene files in the Unity project.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name_pattern"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Filter scenes by name (regex, case-insensitive). Applied to the file name without extension.",
                    },
                    ["max_results"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = ListScenesLimits.MaxResultsMin,
                        ["maximum"] = ListScenesLimits.MaxResultsMax,
                        ["default"] = ListScenesLimits.MaxResultsDefault,
                        ["description"] = "Maximum number of results to return.",
                    },
                    ["offset"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["default"] = 0,
                        ["description"] = "Number of results to skip. Use 'next_offset' from a truncated response to fetch the next page.",
                    },
                },
                ["additionalProperties"] = false,
            }),
        [ToolNames.OpenScene] = new(
            ToolNames.OpenScene,
            30000,
            60000,
            false,
            "Opens a scene in the Unity Editor.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Asset path of the scene file (e.g. \"Assets/Scenes/Main.unity\").",
                    },
                    ["mode"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = OpenSceneModes.ToJsonArray(),
                        ["default"] = OpenSceneModes.Single,
                        ["description"] = "How to open the scene. 'single' replaces current scene, 'additive' adds to current scene.",
                    },
                },
                ["required"] = new JsonArray("path"),
                ["additionalProperties"] = false,
            }),
        [ToolNames.SaveScene] = new(
            ToolNames.SaveScene,
            30000,
            60000,
            false,
            "Saves the current scene or a specific open scene.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Asset path of a specific open scene to save. If omitted, saves the active scene.",
                    },
                },
                ["additionalProperties"] = false,
            }),
        [ToolNames.CreateScene] = new(
            ToolNames.CreateScene,
            30000,
            60000,
            false,
            "Creates a new scene and saves it to the specified path.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Asset path where the new scene will be saved (e.g. \"Assets/Scenes/NewScene.unity\").",
                    },
                    ["setup"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = CreateSceneSetups.ToJsonArray(),
                        ["default"] = CreateSceneSetups.Default,
                        ["description"] = "'default' includes a camera and light. 'empty' creates a blank scene.",
                    },
                },
                ["required"] = new JsonArray("path"),
                ["additionalProperties"] = false,
            }),
        [ToolNames.FindAssets] = new(
            ToolNames.FindAssets,
            10000,
            30000,
            false,
            "Searches for assets in the project using AssetDatabase filter syntax.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["filter"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "AssetDatabase search filter (e.g. \"t:Material\", \"t:Prefab player\", \"l:MyLabel\").",
                    },
                    ["search_in_folders"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Optional list of folder paths to limit the search scope (e.g. [\"Assets/Prefabs\"]).",
                    },
                    ["max_results"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = FindAssetsLimits.MaxResultsMin,
                        ["maximum"] = FindAssetsLimits.MaxResultsMax,
                        ["default"] = FindAssetsLimits.MaxResultsDefault,
                        ["description"] = "Maximum number of results to return.",
                    },
                    ["offset"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["default"] = 0,
                        ["description"] = "Number of results to skip. Use 'next_offset' from a truncated response to fetch the next page.",
                    },
                },
                ["required"] = new JsonArray("filter"),
                ["additionalProperties"] = false,
            }),
        [ToolNames.FindGameObjects] = new(
            ToolNames.FindGameObjects,
            10000,
            30000,
            false,
            "Searches for GameObjects in the active scene or a Prefab asset by name, tag, or component type.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["prefab_path"] = PrefabPathProperty("Omit for active scene."),
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name filter (regex pattern, case-insensitive).",
                    },
                    ["tag"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Tag filter (exact match).",
                    },
                    ["component_type"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Component type name to filter by.",
                    },
                    ["root_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Hierarchy path of a root GameObject to limit the search scope.",
                    },
                    ["layer"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["maximum"] = 31,
                        ["description"] = "Layer index to filter by (exact match).",
                    },
                    ["active"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Filter by active state.",
                    },
                    ["max_results"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = FindSceneGameObjectsLimits.MaxResultsMin,
                        ["maximum"] = FindSceneGameObjectsLimits.MaxResultsMax,
                        ["default"] = FindSceneGameObjectsLimits.MaxResultsDefault,
                        ["description"] = "Maximum number of results to return.",
                    },
                    ["offset"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["default"] = 0,
                        ["description"] = "Number of results to skip. Use 'next_offset' from a truncated response to fetch the next page.",
                    },
                },
                ["additionalProperties"] = false,
            }),
        [ToolNames.InstantiatePrefab] = new(
            ToolNames.InstantiatePrefab,
            10000,
            30000,
            false,
            "Instantiates a Prefab asset into the active scene, maintaining the Prefab link.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["prefab_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Asset path of the Prefab (e.g. \"Assets/Prefabs/Player.prefab\").",
                    },
                    ["parent_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Scene hierarchy path of the parent GameObject. Omit for scene root.",
                    },
                    ["position"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "World position {x, y, z}. Default: Prefab's original position.",
                        ["properties"] = new JsonObject
                        {
                            ["x"] = new JsonObject { ["type"] = "number" },
                            ["y"] = new JsonObject { ["type"] = "number" },
                            ["z"] = new JsonObject { ["type"] = "number" },
                        },
                        ["additionalProperties"] = false,
                    },
                    ["rotation"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "Euler angles {x, y, z}. Default: Prefab's original rotation.",
                        ["properties"] = new JsonObject
                        {
                            ["x"] = new JsonObject { ["type"] = "number" },
                            ["y"] = new JsonObject { ["type"] = "number" },
                            ["z"] = new JsonObject { ["type"] = "number" },
                        },
                        ["additionalProperties"] = false,
                    },
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Override the instantiated GameObject name.",
                    },
                    ["sibling_index"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["description"] = "Position among siblings.",
                    },
                },
                ["required"] = new JsonArray("prefab_path"),
                ["additionalProperties"] = false,
            }),
        [ToolNames.GetAssetInfo] = new(
            ToolNames.GetAssetInfo,
            10000,
            30000,
            false,
            "Returns detailed metadata for a Unity asset at the specified path.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["asset_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Asset path (e.g. \"Assets/Materials/Default.mat\").",
                    },
                },
                ["required"] = new JsonArray("asset_path"),
                ["additionalProperties"] = false,
            }),
        [ToolNames.ManageGameObject] = new(
            ToolNames.ManageGameObject,
            10000,
            30000,
            false,
            "Creates, updates, deletes, or reparents GameObjects in the active scene or a Prefab asset.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["prefab_path"] = PrefabPathProperty("Omit for active scene."),
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = GameObjectActions.ToJsonArray(),
                        ["description"] = "Operation to perform. 'create': creates a new GameObject. 'update': modifies name/tag/layer/active. 'delete': destroys the GameObject and all children. 'reparent': moves to a new parent.",
                    },
                    ["game_object_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Hierarchy path of the target GameObject. Required for update/delete/reparent.",
                    },
                    ["parent_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Parent GameObject path. For create: optional (omit for root). For reparent: new parent path (omit or null for root).",
                    },
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name of the GameObject. Required for create. Optional for update (renames the GO).",
                    },
                    ["tag"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Tag to assign. Optional for create/update.",
                    },
                    ["layer"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["maximum"] = 31,
                        ["description"] = "Layer index (0-31). Optional for create/update.",
                    },
                    ["active"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Active state. Optional for create/update. Default: true for create.",
                    },
                    ["primitive_type"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = PrimitiveTypes.ToJsonArray(),
                        ["description"] = "Creates a Unity primitive. Optional for create only.",
                    },
                    ["world_position_stays"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["default"] = true,
                        ["description"] = "Preserve world position during reparent. Optional for reparent.",
                    },
                    ["sibling_index"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["description"] = "Position among siblings. Optional for create/reparent.",
                    },
                },
                ["required"] = new JsonArray("action"),
                ["additionalProperties"] = false,
            }),
        [ToolNames.ManageAsset] = new(
            ToolNames.ManageAsset,
            15000,
            30000,
            false,
            "Creates or deletes Unity assets (materials, folders, physic materials, animator controllers, render textures). Also manages material properties: get/set shader properties, change shaders, and control shader keywords.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = ManageAssetActions.ToJsonArray(),
                        ["description"] = "Operation to perform. 'create': requires asset_type. 'delete': removes the asset. 'get_properties'/'set_properties'/'set_shader'/'get_keywords'/'set_keywords': material operations.",
                    },
                    ["asset_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Asset path (e.g. \"Assets/Materials/NewMat.mat\").",
                    },
                    ["asset_type"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = AssetTypes.ToJsonArray(),
                        ["description"] = "Type of asset to create. Required for 'create' action.",
                    },
                    ["properties"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true,
                        ["description"] = "Type-specific settings for 'create' (Material: { shader_name }, RenderTexture: { width, height, depth }). Property name-value map for 'set_properties'.",
                    },
                    ["overwrite"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["default"] = false,
                        ["description"] = "If true, overwrites an existing asset. Only applies to 'create' action.",
                    },
                    ["shader_name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Shader name for 'set_shader' action.",
                    },
                    ["keywords"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Shader keywords for 'set_keywords'.",
                    },
                    ["keywords_action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = KeywordsActions.ToJsonArray(),
                        ["description"] = "Whether to enable or disable keywords.",
                    },
                },
                ["required"] = new JsonArray("action", "asset_path"),
                ["additionalProperties"] = false,
            }),
        [ToolNames.CaptureScreenshot] = new(
            ToolNames.CaptureScreenshot,
            15000,
            60000,
            false,
            "Captures a screenshot from Game View or Scene View and saves it as a PNG file.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["source"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = ScreenshotSources.ToJsonArray(),
                        ["default"] = ScreenshotSources.GameView,
                        ["description"] = "Capture source: 'game_view' or 'scene_view'.",
                    },
                    ["width"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = ScreenshotLimits.WidthMin,
                        ["maximum"] = ScreenshotLimits.WidthMax,
                        ["default"] = ScreenshotLimits.WidthDefault,
                        ["description"] = "Width of the screenshot in pixels.",
                    },
                    ["height"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = ScreenshotLimits.HeightMin,
                        ["maximum"] = ScreenshotLimits.HeightMax,
                        ["default"] = ScreenshotLimits.HeightDefault,
                        ["description"] = "Height of the screenshot in pixels.",
                    },
                    ["camera_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Scene hierarchy path of a Camera to use (game_view only). Defaults to Camera.main.",
                    },
                    ["output_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "File path to save the PNG. Defaults to <project>/Screenshots/unity_screenshot_<timestamp>.png.",
                    },
                },
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

    /// <summary>
    /// Builds Unity capability tools for the Plugin wire protocol.
    /// Unified MCP entries are expanded back into their Scene/Prefab wire names.
    /// </summary>
    public static JsonArray BuildUnityCapabilityTools()
    {
        var tools = new JsonArray();
        foreach (var tool in Items.Values)
        {
            if (McpToWireExpansion.TryGetValue(tool.Name, out var wireNames))
            {
                foreach (var wireName in wireNames)
                {
                    tools.Add(BuildCapabilityEntry(wireName, tool));
                }
            }
            else
            {
                tools.Add(BuildCapabilityEntry(tool.Name, tool));
            }
        }

        return tools;
    }

    public static int DefaultTimeoutMs(string toolName)
    {
        if (Items.TryGetValue(toolName, out var metadata))
        {
            return metadata.DefaultTimeoutMs;
        }

        if (WireToMcpAlias.TryGetValue(toolName, out var mcpName) && Items.TryGetValue(mcpName, out metadata))
        {
            return metadata.DefaultTimeoutMs;
        }

        throw new McpException(ErrorCodes.UnknownCommand, $"Unknown tool: {toolName}");
    }

    /// <summary>Maps wire protocol tool names to their unified MCP name for timeout lookup.</summary>
    private static readonly IReadOnlyDictionary<string, string> WireToMcpAlias = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ToolNames.GetSceneHierarchy] = ToolNames.GetHierarchy,
        [ToolNames.GetPrefabHierarchy] = ToolNames.GetHierarchy,
        [ToolNames.GetSceneComponentInfo] = ToolNames.GetComponentInfo,
        [ToolNames.GetPrefabComponentInfo] = ToolNames.GetComponentInfo,
        [ToolNames.ManageSceneComponent] = ToolNames.ManageComponent,
        [ToolNames.ManagePrefabComponent] = ToolNames.ManageComponent,
        [ToolNames.FindSceneGameObjects] = ToolNames.FindGameObjects,
        [ToolNames.FindPrefabGameObjects] = ToolNames.FindGameObjects,
        [ToolNames.ManageSceneGameObject] = ToolNames.ManageGameObject,
        [ToolNames.ManagePrefabGameObject] = ToolNames.ManageGameObject,
    };

    /// <summary>Maps unified MCP tool names to their wire protocol expansions for Plugin capability reporting.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> McpToWireExpansion = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [ToolNames.GetHierarchy] = [ToolNames.GetSceneHierarchy, ToolNames.GetPrefabHierarchy],
        [ToolNames.GetComponentInfo] = [ToolNames.GetSceneComponentInfo, ToolNames.GetPrefabComponentInfo],
        [ToolNames.ManageComponent] = [ToolNames.ManageSceneComponent, ToolNames.ManagePrefabComponent],
        [ToolNames.FindGameObjects] = [ToolNames.FindSceneGameObjects, ToolNames.FindPrefabGameObjects],
        [ToolNames.ManageGameObject] = [ToolNames.ManageSceneGameObject, ToolNames.ManagePrefabGameObject],
    };

    private static JsonObject BuildCapabilityEntry(string wireName, ToolMetadata tool)
    {
        return new JsonObject
        {
            ["name"] = wireName,
            ["default_timeout_ms"] = tool.DefaultTimeoutMs,
            ["max_timeout_ms"] = tool.MaxTimeoutMs,
            ["requires_client_request_id"] = tool.RequiresClientRequestId,
        };
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

    private static JsonObject PrefabPathProperty(string suffix)
    {
        return new JsonObject
        {
            ["type"] = "string",
            ["description"] = $"Asset path of the Prefab (e.g. \"Assets/Prefabs/Player.prefab\"). {suffix}",
        };
    }
}
