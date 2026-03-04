namespace UnityMcpPlugin
{
    internal static class Wire
    {
        internal const int ProtocolVersion = 1;
        internal const string PluginVersion = "0.1.0";
        internal const int EditorStatusIntervalMs = 5000;

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
                case EditorBridgeState.EnteringPlayMode:
                    return "entering_play_mode";
                default:
                    return "ready";
            }
        }

    }
}
