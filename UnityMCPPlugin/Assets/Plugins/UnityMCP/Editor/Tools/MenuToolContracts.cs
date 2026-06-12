using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace UnityMcpPlugin.Tools
{
    internal static class MenuToolErrors
    {
        internal const string EditorBusy = "ERR_INVALID_STATE";
    }

    internal static class ListMenuItemsLimits
    {
        internal const int MaxResultsDefault = 200;
        internal const int MaxResultsMax = 2000;
    }

    /// <summary>
    /// Editor をブロックする(モーダル/ネイティブ OS ダイアログを開く・Editor を終了する)既知メニューの一覧。
    /// EditorApplication.ExecuteMenuItem はダイアログが閉じるまでメインスレッドを停止させるため、
    /// これらを実行すると MCP ブリッジが応答不能になる。execute では実行拒否し、list では既定で除外する。
    /// 完全一致または "entry/" で始まるサブメニューを前方一致で判定する(大文字小文字無視)。
    /// 全ネイティブメニューを網羅はできないため、危険性が判明し次第ここへ追加する。
    /// </summary>
    internal static class MenuBlocklist
    {
        private static readonly string[] BlockedPrefixes =
        {
            "File/Quit",
            "File/New Scene",
            "File/Open Scene",
            "File/Open Recent Scene",
            "File/Save As...",
            "File/Save As Scene Template...",
            "File/Build And Run",
            "Assets/Import New Asset...",
            "Assets/Import Package",
            "Assets/Export Package...",
            "Assets/Reimport All",
            "Assets/Delete",
            "Help/About Unity",
            "Help/Manage License...",
        };

        internal static bool IsBlocked(string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath))
            {
                return false;
            }

            foreach (var prefix in BlockedPrefixes)
            {
                if (menuPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                    menuPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// メニューパスの正規化。
    /// [MenuItem] の menuItem 文字列は末尾にショートカット指定(空白区切りで %=Ctrl/Cmd, #=Shift, &amp;=Alt, _=修飾なし)を含む。
    /// 例: "File/New Scene %n" → "File/New Scene"。ExecuteMenuItem も deny 判定もショートカット無しのパスで行う必要があるため除去する。
    /// </summary>
    internal static class MenuPath
    {
        internal static string StripShortcut(string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath))
            {
                return menuPath;
            }

            var lastSpace = menuPath.LastIndexOf(' ');
            if (lastSpace < 0 || lastSpace == menuPath.Length - 1)
            {
                return menuPath;
            }

            var first = menuPath[lastSpace + 1];
            if (first == '%' || first == '#' || first == '&' || first == '_')
            {
                return menuPath.Substring(0, lastSpace).TrimEnd();
            }

            return menuPath;
        }
    }

    /// <summary>
    /// メニュー項目の定義元の分類。
    /// project = ソースが Assets/ 配下(プロジェクト独自定義)、package = Packages/ 配下、
    /// builtin = Unity 組み込み(precompiled DLL、CompilationPipeline のソースアセンブリに無い)。
    /// </summary>
    internal static class MenuItemOrigins
    {
        internal const string Project = "project";
        internal const string Package = "package";
        internal const string Builtin = "builtin";

        internal static bool IsValid(string value)
        {
            return value == Project || value == Package || value == Builtin;
        }

        // 同一パスが複数アセンブリで定義された場合の優先順位(小さいほど優先)。project を最優先で残す。
        internal static int Rank(string origin)
        {
            switch (origin)
            {
                case Project: return 0;
                case Package: return 1;
                default: return 2;
            }
        }
    }

    /// <summary>列挙直後の生のメニュー項目(ショートカット未除去・定義元付き)。</summary>
    internal readonly struct MenuItemRaw
    {
        internal MenuItemRaw(string path, string origin)
        {
            Path = path;
            Origin = origin;
        }

        internal string Path { get; }
        internal string Origin { get; }
    }

    /// <summary>
    /// メニュー一覧のフィルタ・ページングを行う純粋ロジック。
    /// Editor 依存(TypeCache での列挙・定義元判定)から分離して単体テスト可能にする。
    /// </summary>
    internal static class MenuItemLister
    {
        internal static MenuItemSelection Select(
            IEnumerable<MenuItemRaw> items,
            string pattern,
            bool includeBlocked,
            string sourceFilter,
            int offset,
            int maxResults)
        {
            if (sourceFilter != null && !MenuItemOrigins.IsValid(sourceFilter))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"source must be one of: {MenuItemOrigins.Project}, {MenuItemOrigins.Package}, {MenuItemOrigins.Builtin}");
            }

            Regex regex = pattern != null ? UserRegex.Compile(pattern, "pattern") : null;

            if (maxResults < 1)
            {
                maxResults = 1;
            }
            else if (maxResults > ListMenuItemsLimits.MaxResultsMax)
            {
                maxResults = ListMenuItemsLimits.MaxResultsMax;
            }

            if (offset < 0)
            {
                offset = 0;
            }

            // ショートカット除去 → 同一パスは定義元優先順位で1つに集約 → パス順
            var distinctSorted = items
                .Where(r => !string.IsNullOrEmpty(r.Path))
                .Select(r => new MenuItemRaw(MenuPath.StripShortcut(r.Path), r.Origin))
                .GroupBy(r => r.Path, StringComparer.Ordinal)
                .Select(g => new MenuItemRaw(g.Key, g.OrderBy(r => MenuItemOrigins.Rank(r.Origin)).First().Origin))
                .OrderBy(r => r.Path, StringComparer.Ordinal);

            var matched = new List<MenuItemEntry>();
            foreach (var item in distinctSorted)
            {
                var blocked = MenuBlocklist.IsBlocked(item.Path);
                if (blocked && !includeBlocked)
                {
                    continue;
                }

                if (sourceFilter != null && item.Origin != sourceFilter)
                {
                    continue;
                }

                if (regex != null && !UserRegex.IsMatch(regex, item.Path, "pattern"))
                {
                    continue;
                }

                matched.Add(new MenuItemEntry(item.Path, item.Origin, blocked));
            }

            var totalCount = matched.Count;
            var startIndex = Math.Min(offset, totalCount);
            var endIndex = Math.Min(startIndex + maxResults, totalCount);
            var page = matched.GetRange(startIndex, endIndex - startIndex);

            var truncated = endIndex < totalCount;
            int? nextOffset = truncated ? endIndex : (int?)null;
            return new MenuItemSelection(page, totalCount, truncated, nextOffset);
        }
    }

    internal readonly struct MenuItemSelection
    {
        internal MenuItemSelection(IReadOnlyList<MenuItemEntry> items, int totalCount, bool truncated, int? nextOffset)
        {
            Items = items;
            TotalCount = totalCount;
            Truncated = truncated;
            NextOffset = nextOffset;
        }

        internal IReadOnlyList<MenuItemEntry> Items { get; }
        internal int TotalCount { get; }
        internal bool Truncated { get; }
        internal int? NextOffset { get; }
    }

    internal sealed record MenuItemEntry(
        [property: JsonProperty("path")] string Path,
        [property: JsonProperty("source")] string Source,
        [property: JsonProperty("blocked")] bool Blocked);

    internal sealed record ListMenuItemsPayload(
        [property: JsonProperty("items")] IReadOnlyList<MenuItemEntry> Items,
        [property: JsonProperty("count")] int Count,
        [property: JsonProperty("total_count")] int TotalCount,
        [property: JsonProperty("truncated")] bool Truncated,
        [property: JsonProperty("next_offset", NullValueHandling = NullValueHandling.Ignore)] int? NextOffset);

    internal sealed record ExecuteMenuItemPayload(
        [property: JsonProperty("menu_path")] string MenuPath,
        [property: JsonProperty("executed")] bool Executed,
        [property: JsonProperty("blocking")] bool Blocking,
        [property: JsonProperty("warning", NullValueHandling = NullValueHandling.Ignore)] string Warning);
}
