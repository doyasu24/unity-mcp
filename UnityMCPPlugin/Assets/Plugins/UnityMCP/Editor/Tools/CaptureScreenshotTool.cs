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
                    $"source must be {ScreenshotSources.GameView}|{ScreenshotSources.SceneView}");
            }

            var width = Payload.GetInt(parameters, "width") ?? 1920;
            var height = Payload.GetInt(parameters, "height") ?? 1080;
            var outputPath = Payload.GetString(parameters, "output_path");
            if (string.IsNullOrEmpty(outputPath))
            {
                var timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var screenshotsDir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, "..", "Screenshots"));
                outputPath = System.IO.Path.Combine(screenshotsDir, $"unity_screenshot_{timestamp}.png");
            }

            Camera camera = ResolveCamera(source, parameters);
            if (camera == null)
            {
                throw new PluginException("ERR_OBJECT_NOT_FOUND",
                    "No main camera found. Tag a camera as MainCamera or specify camera_path.");
            }

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

                var directory = System.IO.Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

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

        private static Camera ResolveCamera(string source, JObject parameters)
        {
            if (source == ScreenshotSources.SceneView)
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    throw new PluginException("ERR_INVALID_STATE",
                        "No active Scene View found. Open a Scene View window first.");
                }

                return sceneView.camera;
            }

            var cameraPath = Payload.GetString(parameters, "camera_path");
            if (!string.IsNullOrEmpty(cameraPath))
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

            return Camera.main;
        }
    }
}
