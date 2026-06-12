using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// Editor のメニュー項目をパス指定で実行するツール。
    /// EditorApplication.ExecuteMenuItem を呼ぶ。コンパイル中/アセットインポート中は実行を拒否する。
    /// ブロッキングメニュー(MenuBlocklist: モーダル/Editor 終了)は拒否せず実行し、payload の blocking=true と
    /// warning で「ブロッキングメニューである」旨をクライアントへ通知する。
    /// 注意: 真にモーダルなメニューを実行するとダイアログが閉じられるまでメインスレッドが停止するため、
    /// その場合は応答(payload)自体が返らずブリッジがフリーズしうる(実行する以上コードでは回避不能)。
    /// </summary>
    internal sealed class ExecuteMenuItemTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.ExecuteMenuItem;

        public override object Execute(JObject parameters)
        {
            var menuPath = Payload.GetString(parameters, "menu_path");
            if (string.IsNullOrWhiteSpace(menuPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "menu_path is required");
            }

            // 末尾のショートカット指定(" %n" 等)を除去し、blocking 判定と実行を素のパスで揃える。
            menuPath = MenuPath.StripShortcut(menuPath.Trim());

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new PluginException(MenuToolErrors.EditorBusy,
                    "Editor is busy (compiling or importing assets). Retry after it becomes ready.");
            }

            // ブロッキングメニューでも実行する。拒否はせず、実行後に blocking フラグで通知する。
            var blocking = MenuBlocklist.IsBlocked(menuPath);

            var executed = EditorApplication.ExecuteMenuItem(menuPath);
            if (!executed)
            {
                throw new PluginException("ERR_UNITY_EXECUTION",
                    $"Failed to execute menu '{menuPath}'. It may be invalid, disabled, or require a specific selection/context.");
            }

            var warning = blocking
                ? "Blocking menu (modal dialog / quit). If a modal dialog opened, the Editor and MCP bridge stay frozen until it is dismissed manually."
                : null;

            return new ExecuteMenuItemPayload(menuPath, true, blocking, warning);
        }
    }
}
