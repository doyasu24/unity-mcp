using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// LogBuffer からコンソールログを読み取るツール。
    /// メインスレッド不要のため RequiresMainThread=false。
    /// </summary>
    internal sealed class ReadConsoleTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.ReadConsole;

        // LogBuffer はスレッドセーフなのでメインスレッド不要
        public override bool RequiresMainThread => false;

        public override object Execute(JObject parameters)
        {
            var maxEntries = Payload.GetInt(parameters, "max_entries") ?? ToolLimits.ReadConsoleDefaultMaxEntries;
            if (maxEntries < ToolLimits.ReadConsoleMinEntries || maxEntries > ToolLimits.ReadConsoleMaxEntries)
            {
                throw new PluginException(
                    "ERR_INVALID_PARAMS",
                    $"max_entries must be {ToolLimits.ReadConsoleMinEntries}..{ToolLimits.ReadConsoleMaxEntries}");
            }

            HashSet<string> logTypes = null;
            if (parameters?["log_type"] is JArray logTypeArray && logTypeArray.Count > 0)
            {
                logTypes = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var token in logTypeArray)
                {
                    var val = token?.Value<string>();
                    if (!string.IsNullOrEmpty(val))
                    {
                        if (!ConsoleLogTypes.IsSupported(val))
                        {
                            throw new PluginException("ERR_INVALID_PARAMS", $"Invalid log_type: {val}");
                        }

                        logTypes.Add(val);
                    }
                }
            }

            Regex messageRegex = null;
            var messagePattern = Payload.GetString(parameters, "message_pattern");
            if (messagePattern != null)
            {
                try
                {
                    messageRegex = new Regex(messagePattern, RegexOptions.IgnoreCase);
                }
                catch (System.ArgumentException)
                {
                    throw new PluginException("ERR_INVALID_PARAMS", $"Invalid message_pattern regex: {messagePattern}");
                }
            }

            var stackTraceLines = Payload.GetInt(parameters, "stack_trace_lines") ?? ToolLimits.ReadConsoleDefaultStackTraceLines;
            if (stackTraceLines < 0)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "stack_trace_lines must be >= 0");
            }

            var deduplicate = Payload.GetBool(parameters, "deduplicate") ?? true;
            var offset = Payload.GetInt(parameters, "offset") ?? 0;
            if (offset < 0)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "offset must be >= 0");
            }

            return LogBuffer.Read(maxEntries, logTypes, messageRegex, stackTraceLines, deduplicate, offset);
        }
    }
}
