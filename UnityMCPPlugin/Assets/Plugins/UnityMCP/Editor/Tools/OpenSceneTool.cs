using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// 指定パスのシーンを開くツール。
    /// </summary>
    internal sealed class OpenSceneTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.OpenScene;

        public override object Execute(JObject parameters)
        {
            var path = Payload.GetString(parameters, "path");
            if (string.IsNullOrEmpty(path))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "path is required");
            }

            var modeStr = Payload.GetString(parameters, "mode") ?? OpenSceneModes.Single;
            if (!OpenSceneModes.IsSupported(modeStr))
            {
                throw new PluginException("ERR_INVALID_PARAMS", $"mode must be {OpenSceneModes.Single}|{OpenSceneModes.Additive}");
            }

            var activeScene = SceneManager.GetActiveScene();
            if (activeScene.isDirty)
            {
                throw new PluginException("ERR_UNSAVED_CHANGES",
                    "The current scene has unsaved changes. Call save_scene before opening a new scene.");
            }

            var openMode = modeStr == OpenSceneModes.Additive
                ? OpenSceneMode.Additive
                : OpenSceneMode.Single;

            EditorSceneManager.OpenScene(path, openMode);
            return new OpenScenePayload(path, modeStr);
        }
    }
}
