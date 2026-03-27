using System.Text.Json.Nodes;

namespace UnityMcpServer;

internal sealed record ToolMetadata(
    string Name,
    int DefaultTimeoutMs,
    int MaxTimeoutMs,
    bool RequiresClientRequestId,
    string Description,
    JsonObject InputSchema,
    bool MayTriggerRecompile = false);

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
            15000,
            300000,
            false,
            "Refreshes Unity Editor assets. Server monitors editor state and waits for any triggered recompilation to complete. Returns errors (compile errors, import errors) in the 'errors' field when present. Automatically stops play mode if active. " +
            "Set force=true to reimport all assets regardless of timestamps (useful when external tools modified files).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["force"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Force reimport all assets ignoring timestamps. Use when external tools (hooks, scripts) modified files and normal refresh doesn't detect changes. Default: false.",
                    },
                },
                ["additionalProperties"] = false,
            },
            MayTriggerRecompile: true),
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
                        ["description"] = "start (aliases: play, resume, unpause) | stop (aliases: end, exit) | pause",
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
            "Runs Unity tests and returns the result. Automatically refreshes assets and waits for recompilation before running tests. Returns errors if compilation fails.",
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
                    ["test_full_name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Fully qualified test name for exact match (e.g. 'MyFixture.MyTest(1)'). Maps to TestRunnerApi testNames. Mutually exclusive with test_name_pattern.",
                    },
                    ["test_name_pattern"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Regex pattern to match test names (e.g. '^MyNamespace\\\\.' to run all tests in a namespace). Maps to TestRunnerApi groupNames. Mutually exclusive with test_full_name.",
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
                        ["description"] = "Hierarchy path of a GameObject to scope traversal (e.g. \"/Canvas\" or \"Player/Weapon\"). Only this GameObject and its descendants are returned. Omit to return the entire scene. NOTE: this parameter is 'root_path', not 'game_object_path'.",
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
                        ["description"] = "Maximum number of GameObjects to include. Response is truncated when exceeded. Use 'root_path' to drill into a subtree.",
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
                        ["description"] = "Filter to GameObjects with at least one of these component types (matches Type.Name or FullName, case-insensitive). Tree mode (offset=0): non-matching GOs keep structure but omit components. Flat mode (offset>0): non-matching GOs excluded.",
                    },
                },
                ["additionalProperties"] = false,
            }),
        [ToolNames.GetComponentInfo] = new(
            ToolNames.GetComponentInfo,
            10000,
            30000,
            false,
            "Returns serialized field values of a specific component, or lists all components when index is omitted.",
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
                        ["description"] = "0-based index of the component on the GameObject. When omitted, returns a lightweight list of all components with their indices instead of serialized field values.",
                    },
                    ["fields"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Field names to return. Omit for all serialized fields.",
                    },
                    ["max_array_elements"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = SceneToolLimits.MaxArrayElementsMin,
                        ["maximum"] = SceneToolLimits.MaxArrayElementsMax,
                        ["default"] = SceneToolLimits.MaxArrayElementsDefault,
                        ["description"] = "Max array/List elements to expand per field. 0 returns element count only.",
                    },
                },
                ["required"] = new JsonArray("game_object_path"),
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
                        ["description"] = "Operation to perform. add: requires component_type. update: requires index+fields. remove: requires index. move: requires index+new_index.",
                    },
                    ["game_object_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Hierarchy path of the target GameObject (e.g. \"/Canvas/Panel/Button\" or \"Main Camera\"). Root objects can omit the leading slash.",
                    },
                    ["component_type"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Component type name to add (e.g. \"Rigidbody\", \"UnityEngine.UI.Image\"). Required for 'add'.",
                    },
                    ["index"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["description"] = "0-based component index (from get_hierarchy). Required for update/remove/move. Optional for add (insertion position, >= 1; default: append).",
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
                        ["description"] = "Serialized field name-value map. For 'add' and 'update'.",
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
                        ["description"] = "Folder paths to limit search scope (e.g. [\"Assets/Prefabs\"]).",
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
                        ["description"] = "Name filter (regex, case-insensitive).",
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
            "Creates, updates, deletes, or reparents GameObjects in the active scene or a Prefab asset. When prefab_path is given and the prefab does not exist, 'create' action will create a new prefab file automatically.",
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
                        ["description"] = "Operation to perform. create: new GameObject. update: modify name/tag/layer/active. delete: destroy with children. reparent: move to new parent.",
                    },
                    ["game_object_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Hierarchy path of the target GameObject. Required for update/delete/reparent.",
                    },
                    ["parent_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Parent path. create: omit for scene root. reparent: new parent (omit for root).",
                    },
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name of the GameObject. Required for create. For update: renames.",
                    },
                    ["tag"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Tag to assign. For create/update.",
                    },
                    ["layer"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["maximum"] = 31,
                        ["description"] = "Layer index. For create/update.",
                    },
                    ["active"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Active state. For create/update. Default: true for create.",
                    },
                    ["primitive_type"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = PrimitiveTypes.ToJsonArray(),
                        ["description"] = "Creates a Unity primitive. For create only.",
                    },
                    ["world_position_stays"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["default"] = true,
                        ["description"] = "Preserve world position during reparent.",
                    },
                    ["sibling_index"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["description"] = "Position among siblings. For create/reparent.",
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
            "Manages Unity assets: create/delete (materials, folders, physic materials, animator controllers, render textures) and material operations (get/set properties, shaders, keywords). For prefab creation, use manage_prefab instead.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = ManageAssetActions.ToJsonArray(),
                        ["description"] = "Operation to perform. create: requires asset_type. delete: removes the asset. get_properties/set_properties/set_shader/get_keywords/set_keywords: material operations.",
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
                        ["description"] = "For create: type-specific settings (Material: {shader_name}, RenderTexture: {width, height, depth}). For set_properties: property name-value map.",
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
            "Captures a screenshot from Game View, Scene View, or individual cameras. Returns images inline (base64 PNG) when possible.",
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
                        ["description"] = "Capture source. 'game_view': composited Game View output (requires Play Mode). 'scene_view': Scene View camera render (works in Edit Mode). 'camera': render from a specific camera (requires camera_path).",
                    },
                    ["camera_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Camera hierarchy path. Required for 'camera' source. Ignored for 'game_view' and 'scene_view'.",
                    },
                    ["output_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "File path to save the PNG. Defaults to <project>/Screenshots/unity_screenshot_<timestamp>.png.",
                    },
                },
                ["additionalProperties"] = false,
            }),
        [ToolNames.ExecuteBatch] = new(
            ToolNames.ExecuteBatch,
            600000,
            2400000,
            false,
            "Executes multiple tool calls sequentially in a single MCP request. " +
            "Reduces round-trips when you need several operations together. " +
            "Only execute_batch itself is disallowed (no recursion). " +
            "All other tools including long-running ones (run_tests, refresh_assets) are allowed; " +
            "total latency is the sum of all operations.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["operations"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["tool_name"] = new JsonObject
                                {
                                    ["type"] = "string",
                                },
                                ["arguments"] = new JsonObject
                                {
                                    ["type"] = "object",
                                },
                            },
                            ["required"] = new JsonArray("tool_name"),
                            ["additionalProperties"] = false,
                        },
                        ["minItems"] = 1,
                        ["maxItems"] = ExecuteBatchLimits.MaxOperations,
                    },
                    ["stop_on_error"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["default"] = true,
                        ["description"] = "Stop executing remaining operations on first error.",
                    },
                },
                ["required"] = new JsonArray("operations"),
                ["additionalProperties"] = false,
            }),
        [ToolNames.ManageAsmdef] = new(
            ToolNames.ManageAsmdef,
            120000,
            300000,
            false,
            "Manages Unity Assembly Definition (.asmdef) files: list/get assemblies, create/update/delete definitions, and add/remove assembly references. Identify assemblies by name or GUID (mutually exclusive). Mutation actions wait for recompilation to complete. Returns errors in the 'errors' field when present.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = ManageAsmdefActions.ToJsonArray(),
                        ["description"] = "Operation to perform. list: list all assemblies. get: get detailed info. create: create new asmdef. update: update properties. delete: delete asmdef. add_reference/remove_reference: manage references.",
                    },
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Assembly name. Mutually exclusive with 'guid'. Required for get/update/delete/add_reference/remove_reference (unless guid is specified). Required for create.",
                    },
                    ["guid"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Assembly definition asset GUID. Mutually exclusive with 'name'. Use for get/update/delete/add_reference/remove_reference.",
                    },
                    ["directory"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Directory path for new asmdef (e.g. \"Assets/Scripts/Core\"). Required for 'create' action.",
                    },
                    ["root_namespace"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Root namespace for the assembly.",
                    },
                    ["references"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Assembly references. Accepts names (\"Unity.Mathematics\") and GUID format (\"GUID:xxx\"), can be mixed. For create: initial references. For update: replaces entire list.",
                    },
                    ["use_guids"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["default"] = true,
                        ["description"] = "Store references as GUID format. Only for 'create' action. Default true.",
                    },
                    ["include_platforms"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Platforms to include (e.g. [\"Editor\"]). Empty = all platforms.",
                    },
                    ["exclude_platforms"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Platforms to exclude.",
                    },
                    ["allow_unsafe_code"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Allow unsafe C# code.",
                    },
                    ["auto_referenced"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Auto-reference in project.",
                    },
                    ["define_constraints"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Define constraints.",
                    },
                    ["no_engine_references"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Disable Unity engine references.",
                    },
                    ["reference"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Assembly name to add/remove. Mutually exclusive with 'reference_guid'. For add_reference/remove_reference actions.",
                    },
                    ["reference_guid"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Assembly GUID to add/remove. Mutually exclusive with 'reference'. For add_reference/remove_reference actions.",
                    },
                    ["name_pattern"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Regex filter for assembly names (case-insensitive). For 'list' action.",
                    },
                    ["max_results"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = ManageAsmdefLimits.MaxResultsMin,
                        ["maximum"] = ManageAsmdefLimits.MaxResultsMax,
                        ["default"] = ManageAsmdefLimits.MaxResultsDefault,
                        ["description"] = "Maximum results for 'list' action.",
                    },
                    ["offset"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["default"] = 0,
                        ["description"] = "Skip count for 'list' action.",
                    },
                },
                ["required"] = new JsonArray("action"),
                ["additionalProperties"] = false,
            },
            MayTriggerRecompile: true),

        [ToolNames.ManagePrefab] = new(
            ToolNames.ManagePrefab,
            15000,
            30000,
            false,
            "Manages prefab relationships for scene GameObjects. save: save a scene GameObject as a new prefab asset (or create an empty prefab). apply: apply instance overrides back to the source prefab. unpack: disconnect a prefab instance from its source. get_status: query prefab connection status, overrides, and variant info (read-only).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = ManagePrefabActions.ToJsonArray(),
                        ["description"] = "Operation to perform.",
                    },
                    ["game_object_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Scene hierarchy path of the target GameObject (e.g. \"/Canvas/Panel\"). Required for apply, unpack, get_status. Optional for save (omit to create an empty prefab).",
                    },
                    ["prefab_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Asset path to save the prefab to (e.g. \"Assets/Prefabs/Player.prefab\"). Required for 'save' action only. Parent directories are created automatically.",
                    },
                    ["connect"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["default"] = false,
                        ["description"] = "If true, the scene instance remains linked to the newly created prefab asset. For 'save' action only.",
                    },
                    ["completely"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["default"] = false,
                        ["description"] = "If true, recursively unpacks all nested prefabs. For 'unpack' action only.",
                    },
                },
                ["required"] = new JsonArray("action"),
                ["additionalProperties"] = false,
            }),
        [ToolNames.ManageBuild] = new(
            ToolNames.ManageBuild,
            600000,
            1800000,
            false,
            "Manages Unity build pipeline. Actions: build (execute BuildPipeline.BuildPlayer), build_report (get last build report with size breakdown — unique feature), validate (pre-build checks for missing scenes/scripts/compile errors — unique feature), get_platform/switch_platform, get_settings/set_settings (defines supports add/remove via defines_action — unique feature), get_scenes/set_scenes, list_profiles/get_active_profile/set_active_profile (Unity 6+ only).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = ManageBuildActions.ToJsonArray(),
                        ["description"] = "Operation to perform.",
                    },
                    ["target"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = BuildTargets.ToJsonArray(),
                        ["description"] = "Build target platform. For build (optional, defaults to active), switch_platform (required).",
                    },
                    ["output_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Output path for the build (e.g. 'Builds/Win64/Game.exe'). Required for 'build' action.",
                    },
                    ["scenes"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Scene paths to include in the build. Omit to use EditorBuildSettings scenes. For 'build' action only.",
                    },
                    ["development"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["default"] = false,
                        ["description"] = "Enable development build. For 'build' action only.",
                    },
                    ["options"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = BuildOptionNames.ToJsonArray(),
                        },
                        ["description"] = "Additional build options. For 'build' action only.",
                    },
                    ["subtarget"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("player", "server"),
                        ["description"] = "Build subtarget ('player' or 'server'). For 'build' action only.",
                    },
                    ["property"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = BuildSettingsProperties.ToJsonArray(),
                        ["description"] = "PlayerSettings property name. Required for get_settings/set_settings.",
                    },
                    ["value"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Value to set. Required for set_settings. For defines: semicolon-separated symbols.",
                    },
                    ["defines_action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = DefinesActions.ToJsonArray(),
                        ["default"] = DefinesActions.Set,
                        ["description"] = "How to apply defines: 'set' (replace all, default), 'add' (append symbols), 'remove' (remove symbols). Only for set_settings with property='defines'.",
                    },
                    ["build_scenes"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["path"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["description"] = "Scene asset path (e.g. 'Assets/Scenes/Main.unity').",
                                },
                                ["enabled"] = new JsonObject
                                {
                                    ["type"] = "boolean",
                                    ["default"] = true,
                                },
                            },
                            ["required"] = new JsonArray("path"),
                            ["additionalProperties"] = false,
                        },
                        ["description"] = "Build scene list with enabled flags. Required for 'set_scenes' action.",
                    },
                    ["profile_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Asset path to a BuildProfile, or 'none' to clear. Required for 'set_active_profile' (Unity 6+ only).",
                    },
                },
                ["required"] = new JsonArray("action"),
                ["additionalProperties"] = false,
            },
            MayTriggerRecompile: true),

        [ToolNames.ManagePlayerPrefs] = new(
            ToolNames.ManagePlayerPrefs,
            5000,
            10000,
            false,
            "Manages Unity PlayerPrefs: get/set/delete key-value pairs, check key existence, or delete all. " +
            "Works in both Edit and Play modes. PlayerPrefs stores string, int, and float types; " +
            "'get' returns all three representations since the stored type is not discoverable.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = ManagePlayerPrefsActions.ToJsonArray(),
                        ["description"] = "Operation to perform. get: returns string/int/float values for a key. set: stores a value (requires key, value, value_type). delete: removes a key. has_key: checks if a key exists. delete_all: removes ALL PlayerPrefs (destructive, no undo).",
                    },
                    ["key"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "The PlayerPrefs key. Required for get, set, delete, has_key.",
                    },
                    ["value"] = new JsonObject
                    {
                        ["type"] = new JsonArray("string", "number"),
                        ["description"] = "Value to store. Required for 'set' action. Interpreted according to value_type.",
                    },
                    ["value_type"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = ManagePlayerPrefsValueTypes.ToJsonArray(),
                        ["default"] = ManagePlayerPrefsValueTypes.String,
                        ["description"] = "Type to use when storing the value. Only used with 'set' action.",
                    },
                },
                ["required"] = new JsonArray("action"),
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
