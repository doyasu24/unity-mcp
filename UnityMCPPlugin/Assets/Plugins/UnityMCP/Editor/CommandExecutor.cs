using System;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

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

            throw new PluginException("ERR_UNKNOWN_COMMAND", $"unsupported tool: {toolName}");
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
