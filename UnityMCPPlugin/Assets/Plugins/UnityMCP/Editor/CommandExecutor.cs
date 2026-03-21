using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityMcpPlugin.Tools;

namespace UnityMcpPlugin
{
    internal sealed class CommandExecutor
    {
        private readonly ToolRegistry _registry;

        internal CommandExecutor(Func<EditorSnapshot> snapshotProvider)
        {
            _registry = new ToolRegistry();
            _registry.DiscoverAndRegister();
            // GetEditorStateTool は snapshotProvider が必要なため自動発見されない
            _registry.RegisterExplicit(new GetEditorStateTool(snapshotProvider));
            PluginLogger.DevInfo($"ToolRegistry: {_registry.Count} handlers registered");
        }

        internal async Task<object> ExecuteToolAsync(string toolName, JObject parameters)
        {
            if (!_registry.TryGetHandler(toolName, out var handler))
            {
                throw new PluginException("ERR_UNKNOWN_COMMAND", $"unsupported tool: {toolName}");
            }

            // SyncToolHandler は Execute を直接呼び出し、Task ラップを回避する。
            // .Result 経由の async ハンドラーデッドロックを防ぐ。
            if (handler is SyncToolHandler syncHandler)
            {
                if (syncHandler.RequiresMainThread)
                {
                    return await MainThreadDispatcher.InvokeAsync(() => syncHandler.Execute(parameters));
                }

                return syncHandler.Execute(parameters);
            }

            return await handler.ExecuteAsync(parameters);
        }
    }
}
