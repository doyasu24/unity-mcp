namespace UnityMcpPlugin
{
    internal static class ToolNames
    {
        internal const string GetEditorState = "get_editor_state";
        internal const string GetPlayModeState = "get_play_mode_state";
        internal const string ReadConsole = "read_console";
        internal const string ClearConsole = "clear_console";
        internal const string RefreshAssets = "refresh_assets";
        internal const string ControlPlayMode = "control_play_mode";
        internal const string RunTests = "run_tests";
        internal const string GetSceneHierarchy = "get_scene_hierarchy";
        internal const string GetSceneComponentInfo = "get_scene_component_info";
        internal const string ManageSceneComponent = "manage_scene_component";
        internal const string GetPrefabHierarchy = "get_prefab_hierarchy";
        internal const string GetPrefabComponentInfo = "get_prefab_component_info";
        internal const string ManagePrefabComponent = "manage_prefab_component";
        internal const string ManageSceneGameObject = "manage_scene_game_object";
        internal const string ManagePrefabGameObject = "manage_prefab_game_object";
        internal const string ListScenes = "list_scenes";
        internal const string OpenScene = "open_scene";
        internal const string SaveScene = "save_scene";
        internal const string CreateScene = "create_scene";
        internal const string FindAssets = "find_assets";
        internal const string GetSelection = "get_selection";
        internal const string SetSelection = "set_selection";
        internal const string FindSceneGameObjects = "find_scene_game_objects";
        internal const string InstantiatePrefab = "instantiate_prefab";
        internal const string GetAssetInfo = "get_asset_info";
        internal const string FindPrefabGameObjects = "find_prefab_game_objects";
        internal const string ManageAsset = "manage_asset";
        internal const string CaptureScreenshot = "capture_screenshot";
        internal const string ManageAsmdef = "manage_asmdef";
    }

    internal static class ToolLimits
    {
        internal const int ReadConsoleDefaultMaxEntries = 10;
        internal const int ReadConsoleMinEntries = 0;
        internal const int ReadConsoleMaxEntries = 2000;
        internal const int ReadConsoleDefaultStackTraceLines = 1;
    }

    internal static class ConsoleLogTypes
    {
        internal const string Log = "log";
        internal const string Warning = "warning";
        internal const string Error = "error";
        internal const string Assert = "assert";
        internal const string Exception = "exception";

        internal static bool IsSupported(string type)
        {
            return type == Log || type == Warning || type == Error || type == Assert || type == Exception;
        }
    }

    internal static class RunTestsModes
    {
        internal const string All = "all";
        internal const string Edit = "edit";
        internal const string Play = "play";

        internal static bool IsSupported(string mode)
        {
            return mode == All || mode == Edit || mode == Play;
        }
    }

    internal static class PlayModeActions
    {
        internal const string Start = "start";
        internal const string Stop = "stop";
        internal const string Pause = "pause";

        /// <summary>
        /// LLM が送りがちなエイリアスを正規のアクション名に変換する。
        /// 該当しなければ入力をそのまま返す（後続の IsSupported で弾く）。
        /// </summary>
        internal static string Normalize(string action)
        {
            switch (action?.ToLowerInvariant())
            {
                case "play":
                case "resume":
                case "unpause":
                    return Start;
                case "end":
                case "exit":
                    return Stop;
                case Start:
                case Stop:
                case Pause:
                    return action.ToLowerInvariant();
                default:
                    return action;
            }
        }

        internal static bool IsSupported(string action)
        {
            return action == Start || action == Stop || action == Pause;
        }
    }

    internal static class OpenSceneModes
    {
        internal const string Single = "single";
        internal const string Additive = "additive";

        internal static bool IsSupported(string mode)
        {
            return mode == Single || mode == Additive;
        }
    }

    internal static class CreateSceneSetups
    {
        internal const string Default = "default";
        internal const string Empty = "empty";

        internal static bool IsSupported(string setup)
        {
            return setup == Default || setup == Empty;
        }
    }

    internal static class FindSceneGameObjectsLimits
    {
        internal const int MaxResultsDefault = 100;
        internal const int MaxResultsMax = 1000;
    }

    internal static class ListScenesLimits
    {
        internal const int MaxResultsDefault = 100;
        internal const int MaxResultsMax = 1000;
    }

    internal static class FindAssetsLimits
    {
        internal const int MaxResultsDefault = 100;
        internal const int MaxResultsMax = 1000;
    }

    internal static class PlayModeStates
    {
        internal const string Playing = "playing";
        internal const string Paused = "paused";
        internal const string Stopped = "stopped";
    }

    internal static class ManageAssetActions
    {
        internal const string Create = "create";
        internal const string Delete = "delete";
        internal const string GetProperties = "get_properties";
        internal const string SetProperties = "set_properties";
        internal const string SetShader = "set_shader";
        internal const string GetKeywords = "get_keywords";
        internal const string SetKeywords = "set_keywords";

        internal static bool IsSupported(string action)
        {
            return action == Create || action == Delete || action == GetProperties || action == SetProperties || action == SetShader || action == GetKeywords || action == SetKeywords;
        }
    }

    internal static class AssetTypes
    {
        internal const string Material = "material";
        internal const string Folder = "folder";
        internal const string PhysicMaterial = "physic_material";
        internal const string AnimatorController = "animator_controller";
        internal const string RenderTexture = "render_texture";
        internal const string Prefab = "prefab";

        internal static bool IsSupported(string type)
        {
            return type == Material || type == Folder || type == PhysicMaterial || type == AnimatorController || type == RenderTexture || type == Prefab;
        }
    }

    internal static class KeywordsActions
    {
        internal const string Enable = "enable";
        internal const string Disable = "disable";

        internal static bool IsSupported(string action)
        {
            return action == Enable || action == Disable;
        }
    }

    internal static class ManageAsmdefActions
    {
        internal const string List = "list";
        internal const string Get = "get";
        internal const string Create = "create";
        internal const string Update = "update";
        internal const string Delete = "delete";
        internal const string AddReference = "add_reference";
        internal const string RemoveReference = "remove_reference";

        internal static bool IsSupported(string action)
        {
            return action == List || action == Get || action == Create || action == Update
                || action == Delete || action == AddReference || action == RemoveReference;
        }
    }

    internal static class AsmdefErrors
    {
        internal const string NotFound = "ERR_ASMDEF_NOT_FOUND";
        internal const string AlreadyExists = "ERR_ASMDEF_ALREADY_EXISTS";
        internal const string ReferenceNotFound = "ERR_REFERENCE_NOT_FOUND";
        internal const string DuplicateReference = "ERR_DUPLICATE_REFERENCE";
        internal const string ReferenceNotInAssembly = "ERR_REFERENCE_NOT_IN_ASSEMBLY";
    }

    internal static class ManageAsmdefLimits
    {
        internal const int MaxResultsDefault = 100;
        internal const int MaxResultsMax = 1000;
    }

    internal static class ScreenshotSources
    {
        internal const string GameView = "game_view";
        internal const string SceneView = "scene_view";
        internal const string Camera = "camera";

        internal static bool IsSupported(string source)
        {
            return source == GameView || source == SceneView || source == Camera;
        }
    }
}
