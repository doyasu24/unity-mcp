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
        internal const string GetJobStatus = "get_job_status";
        internal const string Cancel = "cancel";
        internal const string GetSceneHierarchy = "get_scene_hierarchy";
        internal const string GetSceneComponentInfo = "get_scene_component_info";
        internal const string ManageSceneComponent = "manage_scene_component";
        internal const string GetPrefabHierarchy = "get_prefab_hierarchy";
        internal const string GetPrefabComponentInfo = "get_prefab_component_info";
        internal const string ManagePrefabComponent = "manage_prefab_component";
    }

    internal static class ToolLimits
    {
        internal const int ReadConsoleDefaultMaxEntries = 200;
        internal const int ReadConsoleMinEntries = 1;
        internal const int ReadConsoleMaxEntries = 2000;
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

        internal static bool IsSupported(string action)
        {
            return action == Start || action == Stop || action == Pause;
        }
    }

    internal static class PlayModeStates
    {
        internal const string Playing = "playing";
        internal const string Paused = "paused";
        internal const string Stopped = "stopped";
    }
}
