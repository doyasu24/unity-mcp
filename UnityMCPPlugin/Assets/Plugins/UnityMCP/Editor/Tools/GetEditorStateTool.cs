using System;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// EditorSnapshot を返すツール。
    /// コンストラクタに Func&lt;EditorSnapshot&gt; が必要なため自動発見されず、
    /// CommandExecutor が RegisterExplicit で登録する。
    /// </summary>
    internal sealed class GetEditorStateTool : SyncToolHandler
    {
        private readonly Func<EditorSnapshot> _snapshotProvider;

        internal GetEditorStateTool(Func<EditorSnapshot> snapshotProvider)
        {
            _snapshotProvider = snapshotProvider;
        }

        public override string ToolName => ToolNames.GetEditorState;

        // EditorApplication.isCompiling をメインスレッドで確認する
        public override bool RequiresMainThread => true;

        public override object Execute(JObject parameters)
        {
            var snapshot = _snapshotProvider();
            var state = snapshot.State;

            // EditorApplication.isCompiling を権威ある値として使う。
            // EditorStateTracker はイベント駆動で遅延・欠落しうるため、isCompiling で上書きする。
            if (EditorApplication.isCompiling)
            {
                state = EditorBridgeState.Compiling;
            }
            else if (state == EditorBridgeState.Compiling)
            {
                // トラッカーは Compiling だが isCompiling は false → 実際は完了済み
                state = EditorBridgeState.Ready;
            }

            // テスト実行中の場合
            if (state == EditorBridgeState.Ready && RunTestsTool.IsRunning)
            {
                state = EditorBridgeState.RunningTests;
            }

            return new RuntimeStatePayload(
                snapshot.Connected ? "ready" : "waiting_editor",
                Wire.ToWireState(state),
                snapshot.Connected,
                snapshot.Seq);
        }
    }
}
