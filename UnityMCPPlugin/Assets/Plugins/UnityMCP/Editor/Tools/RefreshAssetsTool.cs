using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// AssetDatabase.Refresh を実行し、コンパイル完了を待機するツール。
    /// CompilationPipeline コールバックとメインスレッドディスパッチを組み合わせるため
    /// AsyncToolHandler を使う。
    /// </summary>
    internal sealed class RefreshAssetsTool : AsyncToolHandler
    {
        public override string ToolName => ToolNames.RefreshAssets;

        public override async Task<object> ExecuteAsync(JObject parameters)
        {
            var compilationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var compilationStarted = false;

            Action<object> onStarted = _ => { compilationStarted = true; };
            Action<object> onFinished = null;
            onFinished = _ =>
            {
                CompilationPipeline.compilationStarted -= onStarted;
                CompilationPipeline.compilationFinished -= onFinished;
                compilationTcs.TrySetResult(true);
            };

            await MainThreadDispatcher.InvokeAsync(() =>
            {
                CompilationPipeline.compilationStarted += onStarted;
                CompilationPipeline.compilationFinished += onFinished;
                try
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }
                catch
                {
                    CompilationPipeline.compilationStarted -= onStarted;
                    CompilationPipeline.compilationFinished -= onFinished;
                    throw;
                }

                compilationStarted = compilationStarted || EditorApplication.isCompiling;
                if (!compilationStarted)
                {
                    CompilationPipeline.compilationStarted -= onStarted;
                    CompilationPipeline.compilationFinished -= onFinished;
                    compilationTcs.TrySetResult(true);
                }
                return true;
            });

            await compilationTcs.Task;
            return new RefreshAssetsPayload(true);
        }
    }
}
