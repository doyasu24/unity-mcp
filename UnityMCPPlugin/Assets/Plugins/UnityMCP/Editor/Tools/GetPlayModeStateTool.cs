using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// 現在の Play Mode 状態を返すツール。
    /// EditorApplication のプロパティ参照はメインスレッド必須。
    /// </summary>
    internal sealed class GetPlayModeStateTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.GetPlayModeState;

        public override object Execute(JObject parameters)
        {
            // 共有ヘルパーは ControlPlayModeTool に配置
            return ControlPlayModeTool.BuildPlayModeStatePayload();
        }
    }
}
