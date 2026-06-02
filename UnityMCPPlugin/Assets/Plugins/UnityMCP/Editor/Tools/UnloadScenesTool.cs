using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// 現在開いているシーン構成をスナップショットとして返し、全シーンを空シーンに退避するツール。
    /// シーンファイルを MCP 外で直接編集する前に呼ぶことで、編集対象のシーンを Editor から外し、
    /// 「The open scene(s) have been modified externally」ダイアログを原理的に回避する。
    /// 返した scenes を restore_scenes に渡すと元の構成へ復元できる。
    /// </summary>
    internal sealed class UnloadScenesTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.UnloadScenes;

        public override object Execute(JObject parameters)
        {
            // 退避（NewScene）で失われないよう、未保存・無名シーンが開いていたら拒否する。
            var openScenes = new List<(string Path, bool IsDirty)>(SceneManager.sceneCount);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                openScenes.Add((scene.path, scene.isDirty));
            }

            ValidateUnloadableScenes(openScenes);

            var setup = EditorSceneManager.GetSceneManagerSetup();
            var entries = new List<SceneSetupEntry>(setup.Length);
            foreach (var s in setup)
            {
                entries.Add(new SceneSetupEntry(s.path, s.isActive, s.isLoaded));
            }

            // 全シーンを空シーンに退避する。Editor は最低1シーンを要求するため空シーンを開く。
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            return new UnloadScenesPayload(entries);
        }

        /// <summary>
        /// 退避可能性の検証ロジック。Editor のシーン状態に依存しないため単体テスト可能。
        /// 無名シーンは復元できず、未保存シーンは退避で変更が失われるため、いずれも拒否する。
        /// </summary>
        internal static void ValidateUnloadableScenes(IReadOnlyList<(string Path, bool IsDirty)> openScenes)
        {
            foreach (var scene in openScenes)
            {
                if (string.IsNullOrEmpty(scene.Path))
                {
                    throw new PluginException("ERR_INVALID_PARAMS",
                        "An untitled scene is open. Save it before calling unload_scenes (untitled scenes cannot be restored).");
                }

                if (scene.IsDirty)
                {
                    throw new PluginException("ERR_UNSAVED_CHANGES",
                        $"Scene has unsaved changes: {scene.Path}. Call save_scene before unload_scenes.");
                }
            }
        }
    }
}
