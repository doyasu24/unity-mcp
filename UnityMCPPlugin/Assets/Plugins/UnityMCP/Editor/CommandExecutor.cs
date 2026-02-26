using System;
using System.Text.Json;

namespace UnityMcpPlugin
{
    internal sealed class CommandExecutor
    {
        private readonly Func<EditorSnapshot> _snapshotProvider;

        internal CommandExecutor(Func<EditorSnapshot> snapshotProvider)
        {
            _snapshotProvider = snapshotProvider;
        }

        internal object ExecuteSyncTool(string toolName, JsonElement parameters)
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

            throw new PluginException("ERR_UNKNOWN_COMMAND", $"unsupported tool: {toolName}");
        }
    }
}
