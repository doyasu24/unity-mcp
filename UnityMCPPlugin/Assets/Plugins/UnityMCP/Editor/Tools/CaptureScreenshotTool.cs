using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal sealed class CaptureScreenshotTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.CaptureScreenshot;

        /// <summary>インライン base64 画像の長辺最大ピクセル数。これを超える場合はダウンスケールする。</summary>
        private const int MaxInlineEdge = 960;

        public override object Execute(JObject parameters)
        {
            var source = Payload.GetString(parameters, "source") ?? ScreenshotSources.GameView;
            if (!ScreenshotSources.IsSupported(source))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"source must be {ScreenshotSources.GameView}|{ScreenshotSources.SceneView}|{ScreenshotSources.Camera}");
            }

            var outputPath = Payload.GetString(parameters, "output_path");
            var timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var screenshotsDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..", "Screenshots"));

            switch (source)
            {
                case ScreenshotSources.GameView:
                    return ExecuteGameView(outputPath, screenshotsDir, timestamp);
                case ScreenshotSources.SceneView:
                    return ExecuteSceneView(outputPath, screenshotsDir, timestamp);
                case ScreenshotSources.Camera:
                    return ExecuteCamera(parameters, outputPath, screenshotsDir, timestamp);
                default:
                    throw new PluginException("ERR_INVALID_PARAMS", $"Unsupported source: {source}");
            }
        }

        /// <summary>
        /// game_view: Game View をフォーカス + RepaintImmediately で同期リペイントし、
        /// ScreenCapture API で合成出力（全カメラ・Canvas UI・ポストプロセス含む）をキャプチャする。
        /// </summary>
        private static CaptureScreenshotPayload ExecuteGameView(
            string outputPath, string screenshotsDir, string timestamp)
        {
            if (!EditorApplication.isPlaying)
            {
                throw new PluginException("ERR_INVALID_STATE",
                    "game_view requires Play Mode. Use source='scene_view' to capture the Scene View in Edit Mode, "
                    + "or use control_play_mode to enter Play Mode first.");
            }

            if (string.IsNullOrEmpty(outputPath))
                outputPath = System.IO.Path.Combine(screenshotsDir, $"unity_screenshot_{timestamp}.png");

            // Game View (または Simulator) をフォーカスして ScreenCapture が合成出力を取得できるようにする。
            // GetWindow は存在しない場合に新規作成してしまうため、FindObjectsOfTypeAll で既存ウィンドウのみ検索する。
            var playbackWindow = FindPlaybackWindow();
            if (playbackWindow != null)
            {
                playbackWindow.Focus();
                var repaintMethod = typeof(EditorWindow).GetMethod(
                    "RepaintImmediately", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                repaintMethod?.Invoke(playbackWindow, null);
            }

            Texture2D tex = null;
            try
            {
                tex = ScreenCapture.CaptureScreenshotAsTexture(1);
                if (tex == null)
                {
                    throw new PluginException("ERR_UNITY_EXECUTION",
                        "ScreenCapture.CaptureScreenshotAsTexture returned null. Ensure the Game View is visible.");
                }

                return SaveAndBuildPayload(tex, outputPath, "GameView", ScreenshotSources.GameView);
            }
            finally
            {
                if (tex != null) Object.DestroyImmediate(tex);
            }
        }

        private static CaptureScreenshotPayload ExecuteSceneView(
            string outputPath, string screenshotsDir, string timestamp)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                throw new PluginException("ERR_INVALID_STATE",
                    "No active Scene View found. Open a Scene View window first.");
            }

            if (string.IsNullOrEmpty(outputPath))
                outputPath = System.IO.Path.Combine(screenshotsDir, $"unity_screenshot_{timestamp}.png");

            int width = (int)sceneView.position.width;
            int height = (int)sceneView.position.height;
            return RenderCameraAndSave(sceneView.camera, width, height, outputPath, ScreenshotSources.SceneView);
        }

        private static CaptureScreenshotPayload ExecuteCamera(
            JObject parameters, string outputPath, string screenshotsDir, string timestamp)
        {
            var cameraPath = Payload.GetString(parameters, "camera_path");
            if (string.IsNullOrEmpty(cameraPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    "camera_path is required when source is 'camera'.");
            }

            Camera cam = ResolveExplicitCamera(cameraPath);
            if (string.IsNullOrEmpty(outputPath))
                outputPath = System.IO.Path.Combine(screenshotsDir, $"unity_screenshot_{timestamp}.png");

            int width = cam.pixelWidth > 0 ? cam.pixelWidth : 1920;
            int height = cam.pixelHeight > 0 ? cam.pixelHeight : 1080;
            return RenderCameraAndSave(cam, width, height, outputPath, ScreenshotSources.Camera);
        }

        /// <summary>Camera.Render() + RenderTexture でレンダリングし、保存 + インライン画像を生成する。</summary>
        private static CaptureScreenshotPayload RenderCameraAndSave(
            Camera camera, int width, int height, string outputPath, string source)
        {
            RenderTexture rt = null;
            Texture2D tex = null;
            RenderTexture prevTarget = null;
            try
            {
                rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                prevTarget = camera.targetTexture;
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                return SaveAndBuildPayload(tex, outputPath, camera.name, source);
            }
            finally
            {
                camera.targetTexture = prevTarget;
                RenderTexture.active = null;
                if (rt != null) Object.DestroyImmediate(rt);
                if (tex != null) Object.DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// フルサイズ PNG をディスクに保存し、インライン用にダウンスケールした base64 PNG を生成する。
        /// </summary>
        private static CaptureScreenshotPayload SaveAndBuildPayload(
            Texture2D fullTex, string outputPath, string cameraName, string source)
        {
            // フルサイズをディスクに保存
            byte[] fullPng = ImageConversion.EncodeToPNG(fullTex);
            EnsureDirectoryExists(outputPath);
            System.IO.File.WriteAllBytes(outputPath, fullPng);

            // インライン用 base64 を生成（必要ならダウンスケール）
            string inlineBase64;
            int longestEdge = Mathf.Max(fullTex.width, fullTex.height);
            if (longestEdge <= MaxInlineEdge)
            {
                inlineBase64 = System.Convert.ToBase64String(fullPng);
            }
            else
            {
                inlineBase64 = CreateDownscaledBase64(fullTex);
            }

            return new CaptureScreenshotPayload(
                outputPath, fullTex.width, fullTex.height, cameraName, source, inlineBase64);
        }

        /// <summary>長辺 MaxInlineEdge 以下にダウンスケールして base64 PNG を返す。</summary>
        private static string CreateDownscaledBase64(Texture2D srcTex)
        {
            float scale = (float)MaxInlineEdge / Mathf.Max(srcTex.width, srcTex.height);
            int dstWidth = Mathf.Max(1, Mathf.RoundToInt(srcTex.width * scale));
            int dstHeight = Mathf.Max(1, Mathf.RoundToInt(srcTex.height * scale));

            RenderTexture rt = null;
            Texture2D dstTex = null;
            try
            {
                rt = new RenderTexture(dstWidth, dstHeight, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(srcTex, rt);

                RenderTexture.active = rt;
                dstTex = new Texture2D(dstWidth, dstHeight, TextureFormat.RGB24, false);
                dstTex.ReadPixels(new Rect(0, 0, dstWidth, dstHeight), 0, 0);
                dstTex.Apply();
                RenderTexture.active = null;

                byte[] png = ImageConversion.EncodeToPNG(dstTex);
                return System.Convert.ToBase64String(png);
            }
            finally
            {
                RenderTexture.active = null;
                if (rt != null) Object.DestroyImmediate(rt);
                if (dstTex != null) Object.DestroyImmediate(dstTex);
            }
        }

        /// <summary>camera_path から明示的にカメラを解決する。</summary>
        private static Camera ResolveExplicitCamera(string cameraPath)
        {
            var go = GameObjectResolver.Resolve(cameraPath);
            if (go == null)
            {
                throw new PluginException("ERR_OBJECT_NOT_FOUND",
                    $"GameObject not found at path: {cameraPath}. "
                    + "Verify the path with get_hierarchy, or use source='scene_view' to capture without specifying a camera.");
            }

            var cam = go.GetComponent<Camera>();
            if (cam == null)
            {
                throw new PluginException("ERR_OBJECT_NOT_FOUND",
                    $"No Camera component found on GameObject: {cameraPath}. "
                    + "Use get_component_info to inspect the object's components, or use source='scene_view'.");
            }
            return cam;
        }

        /// <summary>
        /// Game View または Simulator ウィンドウを検索する。
        /// Unity 6 では PlayModeView が GameView/SimulatorWindow の共通基底クラス。
        /// PlayModeView で検索することで、モードに関係なくプレイバックウィンドウを取得できる。
        /// </summary>
        private static EditorWindow FindPlaybackWindow()
        {
            var editorAsm = typeof(EditorWindow).Assembly;

            // PlayModeView は GameView と SimulatorWindow の共通基底（Unity 2019.3+）
            var playModeViewType = editorAsm.GetType("UnityEditor.PlayModeView");
            if (playModeViewType != null)
            {
                var windows = Resources.FindObjectsOfTypeAll(playModeViewType);
                if (windows.Length > 0) return (EditorWindow)windows[0];
            }

            // フォールバック: PlayModeView が見つからない古い Unity 向け
            var gameViewType = editorAsm.GetType("UnityEditor.GameView");
            if (gameViewType != null)
            {
                var windows = Resources.FindObjectsOfTypeAll(gameViewType);
                if (windows.Length > 0) return (EditorWindow)windows[0];
            }

            return null;
        }

        private static void EnsureDirectoryExists(string filePath)
        {
            var directory = System.IO.Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
        }
    }
}
