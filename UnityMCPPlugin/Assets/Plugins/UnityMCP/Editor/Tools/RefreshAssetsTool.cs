using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// AssetDatabase.Refresh を実行し、コンパイル状態を返すツール。
    /// 常に ForceSynchronousImport で同期完了を保証する。
    /// force=true 時は ForceUpdate も併用し、タイムスタンプ無視で全再インポートを強制。
    /// Refresh() 後に isCompiling と scriptCompilationFailed で3分岐。
    /// コンパイル完了は待たない。
    /// </summary>
    internal sealed class RefreshAssetsTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.RefreshAssets;

        public override object Execute(JObject parameters)
        {
            var force = Payload.GetBool(parameters, "force") ?? false;
            var options = force
                ? ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport
                : ImportAssetOptions.ForceSynchronousImport;
            AssetDatabase.Refresh(options);

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
