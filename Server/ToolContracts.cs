using System.Text.Json.Nodes;

namespace UnityMcpServer;

internal static class ToolNames
{
    public const string GetEditorState = "get_editor_state";
    public const string GetPlayModeState = "get_play_mode_state";
    public const string ReadConsole = "read_console";
    public const string ClearConsole = "clear_console";
    public const string RefreshAssets = "refresh_assets";
    public const string ControlPlayMode = "control_play_mode";
    public const string RunTests = "run_tests";
    // Unified MCP tool names (LLM-facing)
    public const string GetHierarchy = "get_hierarchy";
    public const string GetComponentInfo = "get_component_info";
    public const string ManageComponent = "manage_component";
    public const string FindGameObjects = "find_game_objects";
    public const string ManageGameObject = "manage_game_object";

    // Wire protocol tool names (Plugin-facing, kept for UnityBridge)
    public const string GetSceneHierarchy = "get_scene_hierarchy";
    public const string GetSceneComponentInfo = "get_scene_component_info";
    public const string ManageSceneComponent = "manage_scene_component";
    public const string GetPrefabHierarchy = "get_prefab_hierarchy";
    public const string GetPrefabComponentInfo = "get_prefab_component_info";
    public const string ManagePrefabComponent = "manage_prefab_component";
    public const string ManageSceneGameObject = "manage_scene_game_object";
    public const string ManagePrefabGameObject = "manage_prefab_game_object";
    public const string ListScenes = "list_scenes";
    public const string OpenScene = "open_scene";
    public const string SaveScene = "save_scene";
    public const string CreateScene = "create_scene";
    public const string FindAssets = "find_assets";
    public const string FindSceneGameObjects = "find_scene_game_objects";
    public const string FindPrefabGameObjects = "find_prefab_game_objects";
    public const string InstantiatePrefab = "instantiate_prefab";
    public const string GetAssetInfo = "get_asset_info";
    public const string ManageAsset = "manage_asset";
    public const string CaptureScreenshot = "capture_screenshot";
    public const string ExecuteBatch = "execute_batch";
    public const string ManageAsmdef = "manage_asmdef";
    public const string ManagePrefab = "manage_prefab";
}

internal static class ToolLimits
{
    public const int ReadConsoleDefaultMaxEntries = 10;
    public const int ReadConsoleMinEntries = 0;
    public const int ReadConsoleMaxEntries = 2000;
    public const int ReadConsoleDefaultStackTraceLines = 1;
}

internal static class ConsoleLogTypes
{
    public const string Log = "log";
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Assert = "assert";
    public const string Exception = "exception";

    public static bool IsSupported(string? type)
    {
        return type is Log or Warning or Error or Assert or Exception;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Log, Warning, Error, Assert, Exception);
    }
}

internal static class SceneToolLimits
{
    public const int MaxDepthMin = 0;
    public const int MaxDepthMax = 50;
    public const int MaxDepthDefault = 10;
    public const int MaxGameObjectsMin = 1;
    public const int MaxGameObjectsMax = 10000;
    public const int MaxGameObjectsDefault = 1000;
    public const int MaxArrayElementsMin = 0;
    public const int MaxArrayElementsMax = 64;
    public const int MaxArrayElementsDefault = 16;
}

internal static class GameObjectActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Reparent = "reparent";

    public static bool IsSupported(string? action)
    {
        return action is Create or Update or Delete or Reparent;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Create, Update, Delete, Reparent);
    }
}

internal static class PrimitiveTypes
{
    public const string Cube = "Cube";
    public const string Sphere = "Sphere";
    public const string Capsule = "Capsule";
    public const string Cylinder = "Cylinder";
    public const string Plane = "Plane";
    public const string Quad = "Quad";

    public static bool IsSupported(string? type)
    {
        return type is Cube or Sphere or Capsule or Cylinder or Plane or Quad;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Cube, Sphere, Capsule, Cylinder, Plane, Quad);
    }
}

internal static class ManageActions
{
    public const string Add = "add";
    public const string Update = "update";
    public const string Remove = "remove";
    public const string Move = "move";

    public static bool IsSupported(string? action)
    {
        return action is Add or Update or Remove or Move;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Add, Update, Remove, Move);
    }
}

internal static class RunTestsModes
{
    public const string All = "all";
    public const string Edit = "edit";
    public const string Play = "play";

    public static bool IsSupported(string? mode)
    {
        return mode is All or Edit or Play;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(All, Edit, Play);
    }
}

internal static class PlayModeActions
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Pause = "pause";

    /// <summary>
    /// LLM が送りがちなエイリアスを正規のアクション名に変換する。
    /// 該当しなければ入力をそのまま返す（後続の IsSupported で弾く）。
    /// </summary>
    public static string? Normalize(string? action)
    {
        return action?.ToLowerInvariant() switch
        {
            "play" or "resume" or "unpause" => Start,
            "end" or "exit" => Stop,
            // 正規値はそのまま通す
            Start or Stop or Pause => action.ToLowerInvariant(),
            _ => action,
        };
    }

    public static bool IsSupported(string? action)
    {
        return action is Start or Stop or Pause;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Start, Stop, Pause);
    }
}

internal static class OpenSceneModes
{
    public const string Single = "single";
    public const string Additive = "additive";

    public static bool IsSupported(string? mode)
    {
        return mode is Single or Additive;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Single, Additive);
    }
}

internal static class CreateSceneSetups
{
    public const string Default = "default";
    public const string Empty = "empty";

    public static bool IsSupported(string? setup)
    {
        return setup is Default or Empty;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Default, Empty);
    }
}

internal static class FindSceneGameObjectsLimits
{
    public const int MaxResultsMin = 1;
    public const int MaxResultsMax = 1000;
    public const int MaxResultsDefault = 100;
}

internal static class ListScenesLimits
{
    public const int MaxResultsMin = 1;
    public const int MaxResultsMax = 1000;
    public const int MaxResultsDefault = 100;
}

internal static class FindAssetsLimits
{
    public const int MaxResultsMin = 1;
    public const int MaxResultsMax = 1000;
    public const int MaxResultsDefault = 100;
}

internal sealed record ReadConsoleRequest(int MaxEntries, string[]? LogType, string? MessagePattern, int StackTraceLines, bool Deduplicate, int Offset);

internal sealed record GetPlayModeStateRequest();

internal sealed record ControlPlayModeRequest(string Action);

internal sealed record RunTestsRequest(string Mode, string? TestFullName, string? TestNamePattern);

internal sealed record ReadConsoleResult(JsonNode Payload);

internal sealed record GetPlayModeStateResult(JsonNode Payload);

internal sealed record ClearConsoleResult(JsonNode Payload);

internal sealed record RefreshAssetsResult(JsonNode Payload);

internal sealed record ControlPlayModeResult(JsonNode Payload);

internal sealed record RunTestsResult(JsonNode Payload);

internal sealed record ListScenesRequest(string? NamePattern, int MaxResults, int Offset);

internal sealed record GetSceneHierarchyRequest(string? RootPath, int MaxDepth, int MaxGameObjects, int Offset, string[]? ComponentFilter);

internal sealed record GetSceneHierarchyResult(JsonNode Payload);

internal sealed record GetSceneComponentInfoRequest(string GameObjectPath, int? Index, string[]? Fields, int MaxArrayElements);

internal sealed record GetSceneComponentInfoResult(JsonNode Payload);

internal sealed record ManageSceneComponentRequest(string Action, string GameObjectPath, string? ComponentType, int? Index, int? NewIndex, JsonObject? Fields);

internal sealed record ManageSceneComponentResult(JsonNode Payload);

internal sealed record GetPrefabHierarchyRequest(string PrefabPath, string? GameObjectPath, int MaxDepth, int MaxGameObjects, int Offset, string[]? ComponentFilter);

internal sealed record GetPrefabHierarchyResult(JsonNode Payload);

internal sealed record GetPrefabComponentInfoRequest(string PrefabPath, string GameObjectPath, int? Index, string[]? Fields, int MaxArrayElements);

internal sealed record GetPrefabComponentInfoResult(JsonNode Payload);

internal sealed record ManagePrefabComponentRequest(string PrefabPath, string Action, string GameObjectPath, string? ComponentType, int? Index, int? NewIndex, JsonObject? Fields);

internal sealed record ManagePrefabComponentResult(JsonNode Payload);

internal sealed record ManageSceneGameObjectRequest(string Action, string? GameObjectPath, string? ParentPath, string? Name, string? Tag, int? Layer, bool? Active, string? PrimitiveType, bool? WorldPositionStays, int? SiblingIndex);

internal sealed record ManageSceneGameObjectResult(JsonNode Payload);

internal sealed record ManagePrefabGameObjectRequest(string PrefabPath, string Action, string? GameObjectPath, string? ParentPath, string? Name, string? Tag, int? Layer, bool? Active, string? PrimitiveType, bool? WorldPositionStays, int? SiblingIndex);

internal sealed record ManagePrefabGameObjectResult(JsonNode Payload);

internal sealed record ListScenesResult(JsonNode Payload);

internal sealed record OpenSceneRequest(string Path, string Mode);

internal sealed record OpenSceneResult(JsonNode Payload);

internal sealed record SaveSceneRequest(string? Path);

internal sealed record SaveSceneResult(JsonNode Payload);

internal sealed record CreateSceneRequest(string Path, string Setup);

internal sealed record CreateSceneResult(JsonNode Payload);

internal sealed record FindAssetsRequest(string Filter, string[]? SearchInFolders, int MaxResults, int Offset);

internal sealed record FindAssetsResult(JsonNode Payload);

internal sealed record FindSceneGameObjectsRequest(string? Name, string? Tag, string? ComponentType, string? RootPath, int? Layer, bool? Active, int MaxResults, int Offset);

internal sealed record FindSceneGameObjectsResult(JsonNode Payload);

internal sealed record InstantiatePrefabRequest(string PrefabPath, string? ParentPath, JsonObject? Position, JsonObject? Rotation, string? Name, int? SiblingIndex);

internal sealed record InstantiatePrefabResult(JsonNode Payload);

internal sealed record GetAssetInfoRequest(string AssetPath);

internal sealed record GetAssetInfoResult(JsonNode Payload);

internal sealed record FindPrefabGameObjectsRequest(string PrefabPath, string? Name, string? Tag, string? ComponentType, string? RootPath, int? Layer, bool? Active, int MaxResults, int Offset);

internal sealed record FindPrefabGameObjectsResult(JsonNode Payload);

internal static class ManageAssetActions
{
    public const string Create = "create";
    public const string Delete = "delete";
    public const string GetProperties = "get_properties";
    public const string SetProperties = "set_properties";
    public const string SetShader = "set_shader";
    public const string GetKeywords = "get_keywords";
    public const string SetKeywords = "set_keywords";

    public static bool IsSupported(string? action)
    {
        return action is Create or Delete or GetProperties or SetProperties or SetShader or GetKeywords or SetKeywords;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Create, Delete, GetProperties, SetProperties, SetShader, GetKeywords, SetKeywords);
    }
}

internal static class AssetTypes
{
    public const string Material = "material";
    public const string Folder = "folder";
    public const string PhysicMaterial = "physic_material";
    public const string AnimatorController = "animator_controller";
    public const string RenderTexture = "render_texture";

    public static bool IsSupported(string? type)
    {
        return type is Material or Folder or PhysicMaterial or AnimatorController or RenderTexture;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Material, Folder, PhysicMaterial, AnimatorController, RenderTexture);
    }
}

internal sealed record ManageAssetRequest(string Action, string AssetPath, string? AssetType, JsonObject? Properties, bool Overwrite, string? ShaderName = null, string[]? Keywords = null, string? KeywordsAction = null);

internal sealed record ManageAssetResult(JsonNode Payload);

internal static class ScreenshotSources
{
    public const string GameView = "game_view";
    public const string SceneView = "scene_view";
    public const string Camera = "camera";

    public static bool IsSupported(string? source)
    {
        return source is GameView or SceneView or Camera;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(GameView, SceneView, Camera);
    }
}

internal static class ScreenshotLimits
{
    public const int WidthMin = 1;
    public const int WidthMax = 7680;
    public const int WidthDefault = 1920;
    public const int HeightMin = 1;
    public const int HeightMax = 4320;
    public const int HeightDefault = 1080;
}

internal static class KeywordsActions
{
    public const string Enable = "enable";
    public const string Disable = "disable";

    public static bool IsSupported(string? action)
    {
        return action is Enable or Disable;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Enable, Disable);
    }
}

internal sealed record CaptureScreenshotRequest(string Source, int Width, int Height, string? CameraPath, string? OutputPath);

internal sealed record CaptureScreenshotResult(JsonNode Payload);

internal static class ExecuteBatchLimits
{
    public const int MaxOperations = 50;
}

internal static class ManageAsmdefActions
{
    public const string List = "list";
    public const string Get = "get";
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string AddReference = "add_reference";
    public const string RemoveReference = "remove_reference";

    public static bool IsSupported(string? action)
    {
        return action is List or Get or Create or Update or Delete or AddReference or RemoveReference;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(List, Get, Create, Update, Delete, AddReference, RemoveReference);
    }
}

internal static class ManageAsmdefLimits
{
    public const int MaxResultsMin = 1;
    public const int MaxResultsMax = 1000;
    public const int MaxResultsDefault = 100;
}

internal sealed record ManageAsmdefRequest(
    string Action,
    string? Name,
    string? Guid,
    string? Directory,
    string? RootNamespace,
    string[]? References,
    bool? UseGuids,
    string[]? IncludePlatforms,
    string[]? ExcludePlatforms,
    bool? AllowUnsafeCode,
    bool? AutoReferenced,
    string[]? DefineConstraints,
    bool? NoEngineReferences,
    string? Reference,
    string? ReferenceGuid,
    string? NamePattern,
    int MaxResults,
    int Offset);

internal sealed record ManageAsmdefResult(JsonNode Payload);

internal static class ManagePrefabActions
{
    public const string Save = "save";
    public const string Apply = "apply";
    public const string Unpack = "unpack";
    public const string GetStatus = "get_status";

    public static bool IsSupported(string? action)
    {
        return action is Save or Apply or Unpack or GetStatus;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Save, Apply, Unpack, GetStatus);
    }
}

internal sealed record ManagePrefabRequest(
    string Action, string? GameObjectPath, string? PrefabPath,
    bool? Connect, bool? Completely);

internal sealed record ManagePrefabResult(JsonNode Payload);

