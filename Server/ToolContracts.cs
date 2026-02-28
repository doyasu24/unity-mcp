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
    public const string GetJobStatus = "get_job_status";
    public const string CancelJob = "cancel_job";
    public const string GetSceneHierarchy = "get_scene_hierarchy";
    public const string GetSceneComponentInfo = "get_scene_component_info";
    public const string ManageSceneComponent = "manage_scene_component";
    public const string GetPrefabHierarchy = "get_prefab_hierarchy";
    public const string GetPrefabComponentInfo = "get_prefab_component_info";
    public const string ManagePrefabComponent = "manage_prefab_component";
    public const string ManageSceneGameObject = "manage_scene_game_object";
    public const string ManagePrefabGameObject = "manage_prefab_game_object";
}

internal static class ToolLimits
{
    public const int ReadConsoleDefaultMaxEntries = 200;
    public const int ReadConsoleMinEntries = 1;
    public const int ReadConsoleMaxEntries = 2000;
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

    public static bool IsSupported(string? action)
    {
        return action is Start or Stop or Pause;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Start, Stop, Pause);
    }
}

internal sealed record ReadConsoleRequest(int MaxEntries);

internal sealed record GetPlayModeStateRequest();

internal sealed record ControlPlayModeRequest(string Action);

internal sealed record RunTestsRequest(string Mode, string? Filter);

internal sealed record JobStatusRequest(string JobId);

internal sealed record CancelJobRequest(string JobId);

internal sealed record ReadConsoleResult(JsonNode Payload);

internal sealed record GetPlayModeStateResult(JsonNode Payload);

internal sealed record ClearConsoleResult(JsonNode Payload);

internal sealed record RefreshAssetsResult(JsonNode Payload);

internal sealed record ControlPlayModeResult(JsonNode Payload);

internal sealed record RunTestsResult(string JobId, string State);

internal sealed record JobStatusResult(string JobId, string State, JsonNode? Progress, JsonNode Result);

internal sealed record CancelJobResult(string JobId, string Status);

internal sealed record GetSceneHierarchyRequest(string? RootPath, int MaxDepth, int MaxGameObjects);

internal sealed record GetSceneHierarchyResult(JsonNode Payload);

internal sealed record GetSceneComponentInfoRequest(string GameObjectPath, int Index, string[]? Fields, int MaxArrayElements);

internal sealed record GetSceneComponentInfoResult(JsonNode Payload);

internal sealed record ManageSceneComponentRequest(string Action, string GameObjectPath, string? ComponentType, int? Index, int? NewIndex, JsonObject? Fields);

internal sealed record ManageSceneComponentResult(JsonNode Payload);

internal sealed record GetPrefabHierarchyRequest(string PrefabPath, string? GameObjectPath, int MaxDepth, int MaxGameObjects);

internal sealed record GetPrefabHierarchyResult(JsonNode Payload);

internal sealed record GetPrefabComponentInfoRequest(string PrefabPath, string GameObjectPath, int Index, string[]? Fields, int MaxArrayElements);

internal sealed record GetPrefabComponentInfoResult(JsonNode Payload);

internal sealed record ManagePrefabComponentRequest(string PrefabPath, string Action, string GameObjectPath, string? ComponentType, int? Index, int? NewIndex, JsonObject? Fields);

internal sealed record ManagePrefabComponentResult(JsonNode Payload);

internal sealed record ManageSceneGameObjectRequest(string Action, string? GameObjectPath, string? ParentPath, string? Name, string? Tag, int? Layer, bool? Active, string? PrimitiveType, bool? WorldPositionStays, int? SiblingIndex);

internal sealed record ManageSceneGameObjectResult(JsonNode Payload);

internal sealed record ManagePrefabGameObjectRequest(string PrefabPath, string Action, string? GameObjectPath, string? ParentPath, string? Name, string? Tag, int? Layer, bool? Active, string? PrimitiveType, bool? WorldPositionStays, int? SiblingIndex);

internal sealed record ManagePrefabGameObjectResult(JsonNode Payload);
