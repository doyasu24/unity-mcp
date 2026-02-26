namespace UnityMcpPlugin
{
    internal static class Wire
    {
        internal const int ProtocolVersion = 1;
        internal const string PluginVersion = "0.1.0";

        internal static string ToWireState(EditorBridgeState state)
        {
            switch (state)
            {
                case EditorBridgeState.Ready:
                    return "ready";
                case EditorBridgeState.Compiling:
                    return "compiling";
                case EditorBridgeState.Reloading:
                    return "reloading";
                default:
                    return "ready";
            }
        }

        internal static string ToWireState(JobState state)
        {
            switch (state)
            {
                case JobState.Queued:
                    return "queued";
                case JobState.Running:
                    return "running";
                case JobState.Succeeded:
                    return "succeeded";
                case JobState.Failed:
                    return "failed";
                case JobState.Timeout:
                    return "timeout";
                case JobState.Cancelled:
                    return "cancelled";
                default:
                    return "failed";
            }
        }
    }
}
