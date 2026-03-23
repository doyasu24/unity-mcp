using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// AssetDatabase.Refresh を実行するツール。
    /// コンパイルが発生した場合は CompilationPipeline.compilationFinished を待ってから応答する (spec 8.3.3)。
    /// コンパイル待機中にドメインリロードで Plugin が破棄された場合は WebSocket 切断となり、
    /// Server 側の ReconnectTimeout recovery が処理する。
    /// </summary>
    internal sealed class RefreshAssetsTool : AsyncToolHandler
    {
        public override string ToolName => ToolNames.RefreshAssets;

        public override async Task<object> ExecuteAsync(JObject parameters)
        {
            var compilingDetected = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var compilationDone = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            await MainThreadDispatcher.InvokeAsync(() =>
            {
                // イベント購読 → Refresh の順で、Refresh 中の同期発火も捕捉する。
                Action<object> onStarted = null;
                Action<object> onFinished = null;

                onStarted = _ => compilingDetected.TrySetResult(true);
                onFinished = _ =>
                {
                    compilationDone.TrySetResult(true);
                    // メインスレッド上で購読解除
                    CompilationPipeline.compilationStarted -= onStarted;
                    CompilationPipeline.compilationFinished -= onFinished;
                };

                CompilationPipeline.compilationStarted += onStarted;
                CompilationPipeline.compilationFinished += onFinished;

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                // 同一フレームで isCompiling チェック
                if (EditorApplication.isCompiling)
                {
                    compilingDetected.TrySetResult(true);
                }

                // delayCall: 次フレームでの最終判定。
                // AssetDatabase.Refresh 直後の同一フレームでは isCompiling が
                // まだ false の可能性があるため、1フレーム遅延してから確認する。
                EditorApplication.delayCall += () =>
                {
                    if (!compilingDetected.Task.IsCompleted)
                    {
                        // 次フレームでもコンパイル未検知 → コンパイルは発生しない
                        compilingDetected.TrySetResult(false);
                        CompilationPipeline.compilationStarted -= onStarted;
                        CompilationPipeline.compilationFinished -= onFinished;
                    }
                };

                return 0;
            });

            var compiling = await compilingDetected.Task;

            if (compiling)
            {
                // spec 8.3.3: compilationFinished を待機してから応答する。
                // ドメインリロードで Plugin が破棄された場合は TCS が未解決のまま
                // WebSocket 切断 → Server 側 ReconnectTimeout recovery が処理する。
                await compilationDone.Task;
            }

            return new RefreshAssetsPayload(true, compiling);
        }
    }
}
