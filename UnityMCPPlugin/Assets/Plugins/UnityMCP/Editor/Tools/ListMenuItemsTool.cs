using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// 利用可能な Editor メニュー項目を列挙するツール。
    /// 列挙は TypeCache の [MenuItem] 属性スキャンに依る。スクリプト(C#)定義のメニューが対象で、
    /// ネイティブ(C++)定義の項目は列挙されない。ブロッキングメニュー(MenuBlocklist)は
    /// 既定で除外し、include_blocked=true のとき blocked フラグ付きで返す。
    /// 各項目は定義元(project/package/builtin)を source として返し、source で絞り込める。
    /// </summary>
    internal sealed class ListMenuItemsTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.ListMenuItems;

        public override object Execute(JObject parameters)
        {
            var pattern = Payload.GetString(parameters, "pattern");
            var includeBlocked = Payload.GetBool(parameters, "include_blocked") ?? false;
            var source = Payload.GetString(parameters, "source");
            var offset = Payload.GetInt(parameters, "offset") ?? 0;
            var maxResults = Payload.GetInt(parameters, "max_results") ?? ListMenuItemsLimits.MaxResultsDefault;

            var selection = MenuItemLister.Select(CollectMenuItems(), pattern, includeBlocked, source, offset, maxResults);
            return new ListMenuItemsPayload(
                selection.Items,
                selection.Items.Count,
                selection.TotalCount,
                selection.Truncated,
                selection.NextOffset);
        }

        private static IEnumerable<MenuItemRaw> CollectMenuItems()
        {
            var originByAssembly = BuildAssemblyOriginMap();
            var methods = TypeCache.GetMethodsWithAttribute<MenuItem>();
            var items = new List<MenuItemRaw>(methods.Count);
            foreach (var method in methods)
            {
                var assemblyName = method.DeclaringType?.Assembly.GetName().Name;
                // CompilationPipeline のソースアセンブリに無いものは precompiled = Unity 組み込みとみなす
                var origin = assemblyName != null && originByAssembly.TryGetValue(assemblyName, out var o)
                    ? o
                    : MenuItemOrigins.Builtin;

                foreach (var attribute in method.GetCustomAttributes(typeof(MenuItem), false))
                {
                    // validate メソッド(menuItem の有効性判定用)はメニュー本体ではないため除外する
                    if (attribute is MenuItem menuItem && !menuItem.validate && !string.IsNullOrEmpty(menuItem.menuItem))
                    {
                        items.Add(new MenuItemRaw(menuItem.menuItem, origin));
                    }
                }
            }

            return items;
        }

        /// <summary>
        /// アセンブリ名 → 定義元(project/package)のマップを構築する。
        /// CompilationPipeline はプロジェクト/パッケージのソースからコンパイルされるアセンブリのみ返すため、
        /// ソースファイルの先頭パスで Assets/=project, Packages/=package に分類する。
        /// </summary>
        private static Dictionary<string, string> BuildAssemblyOriginMap()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
            {
                var origin = ClassifyBySource(assembly.sourceFiles);
                if (origin != null)
                {
                    map[assembly.name] = origin;
                }
            }

            return map;
        }

        private static string ClassifyBySource(string[] sourceFiles)
        {
            if (sourceFiles == null || sourceFiles.Length == 0)
            {
                return null;
            }

            var first = sourceFiles[0];
            if (first.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return MenuItemOrigins.Project;
            }

            if (first.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return MenuItemOrigins.Package;
            }

            return null;
        }
    }
}
