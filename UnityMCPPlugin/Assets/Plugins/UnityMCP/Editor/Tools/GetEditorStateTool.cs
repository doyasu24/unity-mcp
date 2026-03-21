using System;
using Newtonsoft.Json.Linq;

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

        // EditorSnapshot の取得はスレッドセーフ
        public override bool RequiresMainThread => false;

        public override object Execute(JObject parameters)
        {
            var snapshot = _snapshotProvider();
            return new RuntimeStatePayload(
                snapshot.Connected ? "ready" : "waiting_editor",
                Wire.ToWireState(snapshot.State),
                snapshot.Connected,
                snapshot.Seq);
        }
    }
}
