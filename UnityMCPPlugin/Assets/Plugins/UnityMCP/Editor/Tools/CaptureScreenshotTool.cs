using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal static class CaptureScreenshotTool
    {
        internal static CaptureScreenshotPayload Execute(JObject parameters)
        {
            var source = Payload.GetString(parameters, "source") ?? ScreenshotSources.GameView;
            if (!ScreenshotSources.IsSupported(source))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"source must be {ScreenshotSources.GameView}|{ScreenshotSources.SceneView}|{ScreenshotSources.Camera}");
            }

            var width = Payload.GetInt(parameters, "width") ?? 1920;
            var height = Payload.GetInt(parameters, "height") ?? 1080;
            var outputPath = Payload.GetString(parameters, "output_path");
            var timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var screenshotsDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..", "Screenshots"));

            switch (source)
            {
                case ScreenshotSources.GameView:
                    return ExecuteGameView(width, height, outputPath, screenshotsDir, timestamp, parameters);
                case ScreenshotSources.SceneView:
                    return ExecuteSceneView(width, height, outputPath, screenshotsDir, timestamp);
                case ScreenshotSources.Camera:
                    return ExecuteCamera(parameters, width, height, outputPath, screenshotsDir, timestamp);
                default:
                    throw new PluginException("ERR_INVALID_PARAMS", $"Unsupported source: {source}");
            }
        }

        private static CaptureScreenshotPayload ExecuteGameView(
            int width, int height, string outputPath, string screenshotsDir, string timestamp, JObject parameters)
        {
            if (EditorApplication.isPlaying)
            {
                // Play Mode: ScreenCapture API で合成結果（全カメラ+Canvas UI+ポスプロ）を取得
                if (string.IsNullOrEmpty(outputPath))
                    outputPath = System.IO.Path.Combine(screenshotsDir, $"unity_screenshot_{timestamp}.png");
                return CaptureGameViewPlayMode(outputPath);
            }

            // Edit Mode: Camera.main or camera_path でレンダリング（現行互換）
            Camera camera = ResolveCameraForGameView(parameters);
            if (string.IsNullOrEmpty(outputPath))
                outputPath = System.IO.Path.Combine(screenshotsDir, $"unity_screenshot_{timestamp}.png");
            return RenderCameraToFile(camera, width, height, outputPath, ScreenshotSources.GameView);
        }

        /// <summary>
        /// Play Mode 時に ScreenCapture API で Game View の合成出力を取得する。
        /// Unity ドキュメントは WaitForEndOfFrame 後の呼び出しを推奨するが、Editor のメインスレッドから
        /// 直接呼び出しても現行の Unity バージョンでは動作する。もしキャプチャ結果が黒画像/古いフレームに
        /// なる場合は EditorCoroutine + WaitForEndOfFrame への移行を検討する。
        /// </summary>
        private static CaptureScreenshotPayload CaptureGameViewPlayMode(string outputPath)
        {
            Texture2D tex = null;
            try
            {
                tex = ScreenCapture.CaptureScreenshotAsTexture(1);
                if (tex == null)
                {
                    throw new PluginException("ERR_UNITY_EXECUTION",
                        "ScreenCapture.CaptureScreenshotAsTexture returned null. Ensure the Game View is visible.");
                }

                byte[] pngBytes = ImageConversion.EncodeToPNG(tex);
                EnsureDirectoryExists(outputPath);
                System.IO.File.WriteAllBytes(outputPath, pngBytes);
                return new CaptureScreenshotPayload(outputPath, tex.width, tex.height, "GameView", ScreenshotSources.GameView);
            }
            finally
            {
                if (tex != null) Object.DestroyImmediate(tex);
            }
        }

        private static CaptureScreenshotPayload ExecuteSceneView(
            int width, int height, string outputPath, string screenshotsDir, string timestamp)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                throw new PluginException("ERR_INVALID_STATE",
                    "No active Scene View found. Open a Scene View window first.");
            }

            if (string.IsNullOrEmpty(outputPath))
                outputPath = System.IO.Path.Combine(screenshotsDir, $"unity_screenshot_{timestamp}.png");
            return RenderCameraToFile(sceneView.camera, width, height, outputPath, ScreenshotSources.SceneView);
        }

        private static CaptureScreenshotPayload ExecuteCamera(
            JObject parameters, int width, int height, string outputPath, string screenshotsDir, string timestamp)
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
            return RenderCameraToFile(cam, width, height, outputPath, ScreenshotSources.Camera);
        }

        /// <summary>Camera.Render() + RenderTexture パターンの共通ヘルパー。</summary>
        private static CaptureScreenshotPayload RenderCameraToFile(
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

                byte[] pngBytes = ImageConversion.EncodeToPNG(tex);
                EnsureDirectoryExists(outputPath);
                System.IO.File.WriteAllBytes(outputPath, pngBytes);
                return new CaptureScreenshotPayload(outputPath, width, height, camera.name, source);
            }
            finally
            {
                camera.targetTexture = prevTarget;
                RenderTexture.active = null;
                if (rt != null) Object.DestroyImmediate(rt);
                if (tex != null) Object.DestroyImmediate(tex);
            }
        }

        /// <summary>game_view Edit Mode 用: Camera.main or camera_path でカメラ解決。</summary>
        private static Camera ResolveCameraForGameView(JObject parameters)
        {
            var cameraPath = Payload.GetString(parameters, "camera_path");
            if (!string.IsNullOrEmpty(cameraPath))
                return ResolveExplicitCamera(cameraPath);

            var cam = Camera.main;
            if (cam == null)
            {
                throw new PluginException("ERR_OBJECT_NOT_FOUND",
                    "No main camera found. Tag a camera as MainCamera or specify camera_path.");
            }
            return cam;
        }

        /// <summary>camera_path から明示的にカメラを解決する。</summary>
        private static Camera ResolveExplicitCamera(string cameraPath)
        {
            var go = GameObjectResolver.Resolve(cameraPath);
            if (go == null)
            {
                throw new PluginException("ERR_OBJECT_NOT_FOUND",
                    $"GameObject not found at path: {cameraPath}");
            }

            var cam = go.GetComponent<Camera>();
            if (cam == null)
            {
                throw new PluginException("ERR_OBJECT_NOT_FOUND",
                    $"No Camera component found on GameObject: {cameraPath}");
            }
            return cam;
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
