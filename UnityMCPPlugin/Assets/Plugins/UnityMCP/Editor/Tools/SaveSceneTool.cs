using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// アクティブシーンまたは指定パスのシーンを保存するツール。
    /// </summary>
    internal sealed class SaveSceneTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.SaveScene;

        public override object Execute(JObject parameters)
        {
            var path = Payload.GetString(parameters, "path");
            if (!string.IsNullOrEmpty(path))
            {
                Scene targetScene = default;
                var found = false;
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.path == path)
                    {
                        targetScene = s;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    throw new PluginException("ERR_OBJECT_NOT_FOUND", $"No open scene found with path: {path}");
                }

                EditorSceneManager.SaveScene(targetScene);
                return new SaveScenePayload(targetScene.path);
            }

            var activeScene = SceneManager.GetActiveScene();
            EditorSceneManager.SaveScene(activeScene);
            return new SaveScenePayload(activeScene.path);
        }
    }
}
