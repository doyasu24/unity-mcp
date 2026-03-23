using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// AssetDatabase.Refresh を実行するツール。
    /// コンパイル待機とリカバリはサーバー側 (ExecuteWithRecompileRecoveryAsync) で行うため、
    /// Plugin 側は Refresh を実行して即座に返す。
    /// SyncToolHandler (RequiresMainThread=true) により、ToolRegistry がメインスレッドへのディスパッチを行う。
    /// </summary>
    internal sealed class RefreshAssetsTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.RefreshAssets;

        public override object Execute(JObject parameters)
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            // Refresh 完了直後にメインスレッド上で isCompiling をチェック。
            // コンパイルが既に開始されていれば true を返し、Server 側で完了まで待機させる。
            return new RefreshAssetsPayload(true, EditorApplication.isCompiling);
        }
    }
}
