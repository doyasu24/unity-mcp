using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// 新規シーンを作成して指定パスに保存するツール。
    /// </summary>
    internal sealed class CreateSceneTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.CreateScene;

        public override object Execute(JObject parameters)
        {
            var path = Payload.GetString(parameters, "path");
            if (string.IsNullOrEmpty(path))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "path is required");
            }

            var setupStr = Payload.GetString(parameters, "setup") ?? CreateSceneSetups.Default;
            if (!CreateSceneSetups.IsSupported(setupStr))
            {
                throw new PluginException("ERR_INVALID_PARAMS", $"setup must be {CreateSceneSetups.Default}|{CreateSceneSetups.Empty}");
            }

            var setup = setupStr == CreateSceneSetups.Empty
                ? NewSceneSetup.EmptyScene
                : NewSceneSetup.DefaultGameObjects;

            var scene = EditorSceneManager.NewScene(setup, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, path);
            return new CreateScenePayload(path);
        }
    }
}
