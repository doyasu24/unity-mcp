using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// unload_scenes が返したシーン構成（scenes）を受け取り、元のシーン構成へ復元するツール。
    /// ディスクから開き直すため、退避中に編集されたシーンファイルの内容が反映される。
    /// </summary>
    internal sealed class RestoreScenesTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.RestoreScenes;

        public override object Execute(JObject parameters)
        {
            var entries = ParseSceneEntries(parameters);

            // ロード済みの最初のシーンを Single で開いて退避中の空シーンを置き換える(=アンカー)。
            var anchorIndex = ResolveAnchorIndex(entries);

            EditorSceneManager.OpenScene(entries[anchorIndex].Path, OpenSceneMode.Single);

            for (var i = 0; i < entries.Count; i++)
            {
                if (i == anchorIndex)
                {
                    continue;
                }

                var mode = entries[i].IsLoaded
                    ? OpenSceneMode.Additive
                    : OpenSceneMode.AdditiveWithoutLoading;
                EditorSceneManager.OpenScene(entries[i].Path, mode);
            }

            foreach (var entry in entries)
            {
                if (!entry.IsActive)
                {
                    continue;
                }

                var scene = SceneManager.GetSceneByPath(entry.Path);
                if (scene.IsValid() && scene.isLoaded)
                {
                    SceneManager.SetActiveScene(scene);
                }

                break;
            }

            return new RestoreScenesPayload(entries);
        }

        /// <summary>
        /// scenes パラメータの検証とパース。Editor のシーン状態に依存しないため単体テスト可能。
        /// </summary>
        internal static List<SceneSetupEntry> ParseSceneEntries(JObject parameters)
        {
            if (!(parameters["scenes"] is JArray scenesArray) || scenesArray.Count == 0)
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    "scenes is required (the array returned by unload_scenes).");
            }

            var entries = new List<SceneSetupEntry>(scenesArray.Count);
            foreach (var item in scenesArray)
            {
                var path = Payload.GetString(item, "path");
                if (string.IsNullOrEmpty(path))
                {
                    throw new PluginException("ERR_INVALID_PARAMS", "Each scenes entry requires a non-empty path.");
                }

                var isActive = Payload.GetBool(item, "is_active") ?? false;
                var isLoaded = Payload.GetBool(item, "is_loaded") ?? true;
                entries.Add(new SceneSetupEntry(path, isActive, isLoaded));
            }

            return entries;
        }

        /// <summary>
        /// 退避中の空シーンを置き換えるアンカー（Single で開くシーン）の添字を決める。
        /// ロード済みの最初のシーンを優先し、無ければ先頭にフォールバックする。
        /// </summary>
        internal static int ResolveAnchorIndex(IReadOnlyList<SceneSetupEntry> entries)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].IsLoaded)
                {
                    return i;
                }
            }

            return 0;
        }
    }
}
