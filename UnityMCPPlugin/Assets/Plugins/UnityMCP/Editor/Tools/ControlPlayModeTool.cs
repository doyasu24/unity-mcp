using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// Play Mode を操作するツール (start / stop / pause)。
    /// Play Mode 関連の共有ヘルパーもここに配置し、GetPlayModeStateTool から参照される。
    /// </summary>
    internal sealed class ControlPlayModeTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.ControlPlayMode;

        public override object Execute(JObject parameters)
        {
            var action = Payload.GetString(parameters, "action");
            if (!PlayModeActions.IsSupported(action))
            {
                throw new PluginException(
                    "ERR_INVALID_PARAMS",
                    $"action must be {PlayModeActions.Start}|{PlayModeActions.Stop}|{PlayModeActions.Pause}");
            }

            return ControlPlayMode(action!);
        }

        // ---- 共有ヘルパー (GetPlayModeStateTool からも利用) ----

        internal static PlayModeStatePayload BuildPlayModeStatePayload()
        {
            var isPlaying = EditorApplication.isPlaying;
            var isPaused = EditorApplication.isPaused;
            var isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
            var state = isPlaying
                ? (isPaused ? PlayModeStates.Paused : PlayModeStates.Playing)
                : PlayModeStates.Stopped;

            return new PlayModeStatePayload(
                state,
                isPlaying,
                isPaused,
                isPlayingOrWillChangePlaymode);
        }

        internal static PlayModeControlPayload BuildPlayModePayload(string action)
        {
            return new PlayModeControlPayload(
                action,
                true,
                EditorApplication.isPlaying,
                EditorApplication.isPaused,
                EditorApplication.isPlayingOrWillChangePlaymode);
        }

        private static PlayModeControlPayload ControlPlayMode(string action)
        {
            switch (action)
            {
                case PlayModeActions.Start:
                    EditorApplication.isPaused = false;
                    EditorApplication.isPlaying = true;
                    break;
                case PlayModeActions.Stop:
                    EditorApplication.isPaused = false;
                    EditorApplication.isPlaying = false;
                    break;
                case PlayModeActions.Pause:
                    if (!EditorApplication.isPlaying)
                    {
                        throw new PluginException("ERR_INVALID_STATE", "pause requires play mode");
                    }

                    EditorApplication.isPaused = true;
                    break;
                default:
                    throw new PluginException(
                        "ERR_INVALID_PARAMS",
                        $"action must be {PlayModeActions.Start}|{PlayModeActions.Stop}|{PlayModeActions.Pause}");
            }

            return BuildPlayModePayload(action);
        }
    }
}
