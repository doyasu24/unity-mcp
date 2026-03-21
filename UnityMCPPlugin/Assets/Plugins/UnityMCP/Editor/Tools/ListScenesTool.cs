using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// プロジェクト内のシーンアセットを一覧するツール。
    /// </summary>
    internal sealed class ListScenesTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.ListScenes;

        public override object Execute(JObject parameters)
        {
            var guids = AssetDatabase.FindAssets("t:Scene");
            var allPaths = new List<string>(guids.Length);
            foreach (var guid in guids)
            {
                allPaths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }

            var namePattern = Payload.GetString(parameters, "name_pattern");
            Regex nameRegex = null;
            if (namePattern != null)
            {
                try
                {
                    nameRegex = new Regex(namePattern, RegexOptions.IgnoreCase);
                }
                catch (System.ArgumentException)
                {
                    throw new PluginException("ERR_INVALID_PARAMS", $"Invalid name_pattern regex: {namePattern}");
                }
            }

            var filtered = new List<string>();
            foreach (var path in allPaths)
            {
                if (nameRegex != null)
                {
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (!nameRegex.IsMatch(fileName)) continue;
                }

                filtered.Add(path);
            }

            filtered.Sort(System.StringComparer.Ordinal);

            var maxResults = Payload.GetInt(parameters, "max_results") ?? ListScenesLimits.MaxResultsDefault;
            if (maxResults < 1)
            {
                maxResults = 1;
            }
            else if (maxResults > ListScenesLimits.MaxResultsMax)
            {
                maxResults = ListScenesLimits.MaxResultsMax;
            }

            var offset = Payload.GetInt(parameters, "offset") ?? 0;
            if (offset < 0) offset = 0;

            var totalCount = filtered.Count;
            var startIndex = System.Math.Min(offset, totalCount);
            var endIndex = System.Math.Min(startIndex + maxResults, totalCount);
            var scenes = new List<SceneEntry>(endIndex - startIndex);
            for (var i = startIndex; i < endIndex; i++)
            {
                scenes.Add(new SceneEntry(filtered[i]));
            }

            var truncated = endIndex < totalCount;
            int? nextOffset = truncated ? endIndex : null;
            return new ListScenesPayload(scenes, scenes.Count, totalCount, truncated, nextOffset);
        }
    }
}
