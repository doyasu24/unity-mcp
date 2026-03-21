using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// ManageAssetTool と ManagePrefabGameObjectTool で共有するプレハブ作成ヘルパー。
    /// </summary>
    internal static class PrefabHelper
    {
        /// <summary>
        /// 指定パスに空の GameObject をルートとする新規プレハブを作成する。
        /// ルート名はファイル名（拡張子なし）から取得。
        /// </summary>
        internal static void CreateEmptyPrefab(string prefabPath)
        {
            var rootName = System.IO.Path.GetFileNameWithoutExtension(prefabPath);
            var tempGo = new GameObject(rootName);
            try
            {
                bool success;
                PrefabUtility.SaveAsPrefabAsset(tempGo, prefabPath, out success);
                if (!success)
                {
                    throw new PluginException(PrefabToolErrors.PrefabSaveFailed,
                        $"Failed to create new prefab at: {prefabPath}");
                }
            }
            finally
            {
                Object.DestroyImmediate(tempGo);
            }
        }
    }
}
