using System;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityMcpPlugin.Tools;

namespace UnityMcpPlugin
{
    internal sealed class CommandExecutor
    {
        private readonly Func<EditorSnapshot> _snapshotProvider;

        internal CommandExecutor(Func<EditorSnapshot> snapshotProvider)
        {
            _snapshotProvider = snapshotProvider;
        }

        internal async Task<object> ExecuteToolAsync(string toolName, JObject parameters)
        {
            if (string.Equals(toolName, ToolNames.ReadConsole, StringComparison.Ordinal))
            {
                var maxEntries = Payload.GetInt(parameters, "max_entries") ?? ToolLimits.ReadConsoleDefaultMaxEntries;
                if (maxEntries < ToolLimits.ReadConsoleMinEntries || maxEntries > ToolLimits.ReadConsoleMaxEntries)
                {
                    throw new PluginException(
                        "ERR_INVALID_PARAMS",
                        $"max_entries must be {ToolLimits.ReadConsoleMinEntries}..{ToolLimits.ReadConsoleMaxEntries}");
                }

                return LogBuffer.Read(maxEntries);
            }

            if (string.Equals(toolName, ToolNames.GetEditorState, StringComparison.Ordinal))
            {
                var snapshot = _snapshotProvider();
                return new RuntimeStatePayload(
                    snapshot.Connected ? "ready" : "waiting_editor",
                    Wire.ToWireState(snapshot.State),
                    snapshot.Connected,
                    snapshot.Seq);
            }

            if (string.Equals(toolName, ToolNames.GetPlayModeState, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(BuildPlayModeStatePayload);
            }

            if (string.Equals(toolName, ToolNames.ClearConsole, StringComparison.Ordinal))
            {
                var clearedCount = LogBuffer.Clear();
                await MainThreadDispatcher.InvokeAsync(() =>
                {
                    if (!TryClearUnityConsole())
                    {
                        throw new PluginException("ERR_UNITY_EXECUTION", "failed to clear Unity Console");
                    }

                    return true;
                });

                return new ClearConsolePayload(true, clearedCount);
            }

            if (string.Equals(toolName, ToolNames.RefreshAssets, StringComparison.Ordinal))
            {
                await MainThreadDispatcher.InvokeAsync(() =>
                {
                    AssetDatabase.Refresh();
                    return true;
                });

                return new RefreshAssetsPayload(true);
            }

            if (string.Equals(toolName, ToolNames.ControlPlayMode, StringComparison.Ordinal))
            {
                var action = Payload.GetString(parameters, "action");
                if (!PlayModeActions.IsSupported(action))
                {
                    throw new PluginException(
                        "ERR_INVALID_PARAMS",
                        $"action must be {PlayModeActions.Start}|{PlayModeActions.Stop}|{PlayModeActions.Pause}");
                }

                return await MainThreadDispatcher.InvokeAsync(() => ControlPlayMode(action!));
            }

            if (string.Equals(toolName, ToolNames.GetSceneHierarchy, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => SceneHierarchyTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.GetComponentInfo, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ComponentInfoTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.ManageComponent, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ManageComponentTool.Execute(parameters));
            }

            throw new PluginException("ERR_UNKNOWN_COMMAND", $"unsupported tool: {toolName}");
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

        private static PlayModeStatePayload BuildPlayModeStatePayload()
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

        private static PlayModeControlPayload BuildPlayModePayload(string action)
        {
            return new PlayModeControlPayload(
                action,
                true,
                EditorApplication.isPlaying,
                EditorApplication.isPaused,
                EditorApplication.isPlayingOrWillChangePlaymode);
        }

        private static bool TryClearUnityConsole()
        {
            return TryInvokeClear("UnityEditor.LogEntries, UnityEditor") ||
                   TryInvokeClear("UnityEditorInternal.LogEntries, UnityEditor");
        }

        private static bool TryInvokeClear(string typeName)
        {
            var type = Type.GetType(typeName);
            if (type == null)
            {
                return false;
            }

            var clearMethod = type.GetMethod("Clear", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (clearMethod == null)
            {
                return false;
            }

            clearMethod.Invoke(null, null);
            return true;
        }
    }
}
