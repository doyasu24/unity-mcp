using System;
using System.Collections.Generic;

namespace UnityMcpPlugin
{
    /// <summary>
    /// TypeCache ベースの自動発見でツールハンドラーを登録・ルックアップする。
    /// CommandExecutor から利用される。
    /// </summary>
    internal sealed class ToolRegistry
    {
        private readonly Dictionary<string, IToolHandler> _handlers = new(StringComparer.Ordinal);

        /// <summary>
        /// TypeCache 経由で IToolHandler 実装を自動発見し登録する。
        /// パラメータなしコンストラクタを持つ具象クラスのみ対象。
        /// </summary>
        internal void DiscoverAndRegister()
        {
            var types = UnityEditor.TypeCache.GetTypesDerivedFrom<IToolHandler>();
            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface)
                    continue;

                // パラメータなしコンストラクタが必要
                if (type.GetConstructor(Type.EmptyTypes) == null)
                    continue;

                IToolHandler handler;
                try
                {
                    handler = (IToolHandler)Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    PluginLogger.DevWarn("Failed to instantiate ToolHandler",
                        ("type", type.FullName), ("error", ex.Message));
                    continue;
                }

                if (string.IsNullOrEmpty(handler.ToolName))
                {
                    PluginLogger.DevWarn("ToolHandler has empty ToolName, skipping",
                        ("type", type.FullName));
                    continue;
                }

                if (_handlers.ContainsKey(handler.ToolName))
                {
                    PluginLogger.DevError("Duplicate ToolName detected, skipping",
                        ("toolName", handler.ToolName), ("type", type.FullName));
                    continue;
                }

                _handlers[handler.ToolName] = handler;
            }
        }

        /// <summary>
        /// DI が必要なハンドラーを手動で登録する。
        /// 同じ ToolName の既存ハンドラーがあれば上書きする。
        /// </summary>
        internal void RegisterExplicit(IToolHandler handler)
        {
            _handlers[handler.ToolName] = handler;
        }

        internal bool TryGetHandler(string toolName, out IToolHandler handler)
        {
            return _handlers.TryGetValue(toolName, out handler);
        }

        internal int Count => _handlers.Count;
    }
}
