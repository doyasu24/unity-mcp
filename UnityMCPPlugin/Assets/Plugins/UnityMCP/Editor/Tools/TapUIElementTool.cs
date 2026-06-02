using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// Play Mode 中の uGUI 要素をタップ（ポインタクリック）するツール。
    /// EventSystem 経由で pointerEnter → Down → Up → Click を順に発火し、本物のタップ操作を再現する。
    /// onClick / IPointerClickHandler が同期発火するため、クリックハンドラ内の画面遷移は同じ呼び出しで実行される。
    /// target_path（階層パス）指定と (x, y)（スクリーン座標ヒットテスト）指定の両方に対応する。
    /// </summary>
    internal sealed class TapUIElementTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.TapUIElement;

        public override object Execute(JObject parameters)
        {
            if (!EditorApplication.isPlaying)
            {
                throw new PluginException(
                    "ERR_INVALID_STATE",
                    "tap_ui_element requires Play Mode. Use control_play_mode to enter Play Mode first.");
            }

            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                throw new PluginException(
                    "ERR_INVALID_STATE",
                    "No active EventSystem found. uGUI interaction requires an EventSystem in the scene.");
            }

            // 排他バリデーションはプロパティの「存在」で判定する（値のパース可否ではない）。
            var hasPath = parameters["target_path"] != null;
            var hasX = parameters["x"] != null;
            var hasY = parameters["y"] != null;

            GameObject target;
            Vector2 screenPos;
            if (hasPath)
            {
                if (hasX || hasY)
                {
                    throw new PluginException(
                        "ERR_INVALID_PARAMS",
                        "target_path is mutually exclusive with x/y. Specify exactly one targeting mode.");
                }

                var targetPath = Payload.GetString(parameters, "target_path");
                if (string.IsNullOrEmpty(targetPath))
                {
                    throw new PluginException("ERR_INVALID_PARAMS", "target_path must be a non-empty string.");
                }

                target = GameObjectResolver.Resolve(targetPath);
                if (target == null)
                {
                    throw new PluginException(
                        "ERR_OBJECT_NOT_FOUND",
                        $"GameObject not found at path: {targetPath}. Verify the path with get_hierarchy or find_game_objects "
                        + "(note: inactive GameObjects are not resolvable).");
                }

                screenPos = ComputeScreenPoint(target);
            }
            else
            {
                if (!hasX || !hasY)
                {
                    throw new PluginException(
                        "ERR_INVALID_PARAMS",
                        "Specify either target_path OR both x and y (exactly one targeting mode is required).");
                }

                var x = Payload.GetFloat(parameters, "x");
                var y = Payload.GetFloat(parameters, "y");
                if (!x.HasValue || !y.HasValue)
                {
                    throw new PluginException("ERR_INVALID_PARAMS", "x and y must be numbers.");
                }

                screenPos = new Vector2(x.Value, y.Value);
                target = RaycastTopmost(eventSystem, screenPos);
                if (target == null)
                {
                    throw new PluginException(
                        "ERR_OBJECT_NOT_FOUND",
                        $"No UI element was hit at screen position ({x.Value}, {y.Value}). "
                        + "Ensure a GraphicRaycaster-backed Canvas covers that point.");
                }
            }

            // タップ対象から到達可能なクリックハンドラ（Button 等）を事前検証する。
            // 無ければ「成功扱いの no-op」になるため、明確にエラーを返す。
            var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            if (clickHandler == null)
            {
                throw new PluginException(
                    "ERR_OBJECT_NOT_FOUND",
                    "No IPointerClickHandler (e.g. Button) found on or above the target. The element is not tappable.");
            }

            // dispatch 前に応答用の識別情報を確定する。
            // クリックハンドラがシーン遷移・パネル破棄・EventSystem 入れ替えを行うと、
            // dispatch 後には target / eventSystem が破棄され MissingReferenceException になりうるため。
            var resolvedPath = GameObjectResolver.GetHierarchyPath(target);
            var eventSystemName = eventSystem.gameObject.name;

            var dispatched = DispatchTap(eventSystem, target, clickHandler, screenPos);

            return new TapUIElementPayload(
                true,
                resolvedPath,
                screenPos.x,
                screenPos.y,
                eventSystemName,
                dispatched);
        }

        /// <summary>
        /// 対象 RectTransform の矩形中心のスクリーン座標を算出する。
        /// pivot 依存を避けるため transform.position ではなく rect.center をワールド変換する。
        /// ScreenSpaceOverlay はカメラ null、Camera/WorldSpace は rootCanvas の worldCamera を使う。
        /// このスクリーン座標は主に PointerEventData.position と応答の参考値に使われる
        /// （パスモードのクリック自体は解決済み GameObject へ直接ディスパッチするため座標精度に依存しない）。
        /// </summary>
        private static Vector2 ComputeScreenPoint(GameObject go)
        {
            var canvas = go.GetComponentInParent<Canvas>();
            var worldCenter = go.transform is RectTransform rt
                ? rt.TransformPoint(rt.rect.center)
                : go.transform.position;

            var cam = canvas != null && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.rootCanvas.worldCamera
                : null;
            return RectTransformUtility.WorldToScreenPoint(cam, worldCenter);
        }

        /// <summary>スクリーン座標直下の最前面 UI 要素を GraphicRaycaster 経由で取得する。</summary>
        private static GameObject RaycastTopmost(EventSystem eventSystem, Vector2 screenPos)
        {
            var pointerData = new PointerEventData(eventSystem) { position = screenPos };
            var results = new List<RaycastResult>();
            eventSystem.RaycastAll(pointerData, results);
            return results.Count > 0 ? results[0].gameObject : null;
        }

        /// <summary>
        /// pointerEnter → Down → Up → Click を発火する（StandaloneInputModule 準拠）。
        /// pointerUp は押下を処理したオブジェクトへ送り、Click は事前検証済みのクリックハンドラへ送る。
        /// ExecuteEvents はハンドラ内例外をログ化して握り潰すため、dispatch 中の LogType.Exception を捕捉して surface する。
        /// </summary>
        private static IReadOnlyList<string> DispatchTap(
            EventSystem eventSystem, GameObject target, GameObject clickHandler, Vector2 screenPos)
        {
            var pointerData = new PointerEventData(eventSystem)
            {
                position = screenPos,
                pressPosition = screenPos,
                button = PointerEventData.InputButton.Left,
            };

            var dispatched = new List<string>();
            string capturedException = null;
            Application.LogCallback logHandler = (condition, stackTrace, type) =>
            {
                if (type == LogType.Exception && capturedException == null)
                {
                    capturedException = condition;
                }
            };

            Application.logMessageReceived += logHandler;
            try
            {
                if (ExecuteEvents.ExecuteHierarchy(target, pointerData, ExecuteEvents.pointerEnterHandler) != null)
                {
                    dispatched.Add("pointerEnter");
                }

                // 押下を処理したオブジェクトを記録。誰も処理しなければクリックハンドラを press 対象とする。
                var pressTarget = ExecuteEvents.ExecuteHierarchy(target, pointerData, ExecuteEvents.pointerDownHandler);
                if (pressTarget != null)
                {
                    dispatched.Add("pointerDown");
                }
                else
                {
                    pressTarget = clickHandler;
                }

                pointerData.pointerPress = pressTarget;
                if (ExecuteEvents.Execute(pressTarget, pointerData, ExecuteEvents.pointerUpHandler))
                {
                    dispatched.Add("pointerUp");
                }

                // クリックハンドラは事前検証済みなので必ず発火する（タップの意図を確実に実行する）。
                ExecuteEvents.Execute(clickHandler, pointerData, ExecuteEvents.pointerClickHandler);
                dispatched.Add("pointerClick");
            }
            finally
            {
                Application.logMessageReceived -= logHandler;
            }

            if (capturedException != null)
            {
                throw new PluginException(
                    "ERR_UNITY_EXECUTION",
                    $"A UI handler threw an exception during tap dispatch: {capturedException}");
            }

            return dispatched;
        }
    }
}
