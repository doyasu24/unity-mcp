using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// AssetDatabase.Refresh を実行し、コンパイル状態を返すツール。
    /// Refresh() 後に isCompiling と scriptCompilationFailed で3分岐。
    /// コンパイル完了は待たない。
    /// </summary>
    internal sealed class RefreshAssetsTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.RefreshAssets;

        public override object Execute(JObject parameters)
        {
            AssetDatabase.Refresh();

            if (EditorApplication.isCompiling)
            {
                return new RefreshAssetsPayload(true, true, false);
            }

            if (EditorUtility.scriptCompilationFailed)
            {
                return new RefreshAssetsPayload(true, false, true);
            }

            return new RefreshAssetsPayload(true, false, false);
        }
    }
}
