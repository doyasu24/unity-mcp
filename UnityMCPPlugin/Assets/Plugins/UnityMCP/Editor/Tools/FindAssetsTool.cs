using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// AssetDatabase.FindAssets でアセットを検索するツール。
    /// </summary>
    internal sealed class FindAssetsTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.FindAssets;

        public override object Execute(JObject parameters)
        {
            var filter = Payload.GetString(parameters, "filter");
            if (string.IsNullOrEmpty(filter))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "filter is required");
            }

            var maxResults = Payload.GetInt(parameters, "max_results") ?? FindAssetsLimits.MaxResultsDefault;
            if (maxResults < 1)
            {
                maxResults = 1;
            }
            else if (maxResults > FindAssetsLimits.MaxResultsMax)
            {
                maxResults = FindAssetsLimits.MaxResultsMax;
            }

            var offset = Payload.GetInt(parameters, "offset") ?? 0;

            string[] guids;
            if (parameters["search_in_folders"] is JArray foldersArray && foldersArray.Count > 0)
            {
                var folders = new List<string>(foldersArray.Count);
                foreach (var token in foldersArray)
                {
                    var folder = token?.Value<string>();
                    if (!string.IsNullOrEmpty(folder))
                    {
                        folders.Add(folder);
                    }
                }

                guids = AssetDatabase.FindAssets(filter, folders.ToArray());
            }
            else
            {
                guids = AssetDatabase.FindAssets(filter);
            }

            // パスで解決しソートすることでページネーション順序を安定させる
            var allEntries = new List<AssetEntry>(guids.Length);
            foreach (var guid in guids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                var typeName = assetType != null ? assetType.Name : "Unknown";
                var fileName = System.IO.Path.GetFileName(assetPath);
                allEntries.Add(new AssetEntry(assetPath, typeName, fileName, guid));
            }

            allEntries.Sort((a, b) => string.Compare(a.Path, b.Path, System.StringComparison.Ordinal));

            var totalCount = allEntries.Count;
            var startIndex = System.Math.Min(offset, totalCount);
            var endIndex = System.Math.Min(startIndex + maxResults, totalCount);
            var assets = allEntries.GetRange(startIndex, endIndex - startIndex);

            var truncated = endIndex < totalCount;
            int? nextOffset = truncated ? endIndex : null;
            return new FindAssetsPayload(assets, assets.Count, truncated, totalCount, nextOffset);
        }
    }
}
