namespace UnityMcpPlugin
{
    internal static class ToolNames
    {
        internal const string GetEditorState = "get_editor_state";
        internal const string ReadConsole = "read_console";
        internal const string RunTests = "run_tests";
        internal const string GetJobStatus = "get_job_status";
        internal const string Cancel = "cancel";
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
}
