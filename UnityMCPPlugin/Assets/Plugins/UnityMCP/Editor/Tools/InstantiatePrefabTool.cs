using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal sealed class InstantiatePrefabTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.InstantiatePrefab;

        public override object Execute(JObject parameters)
        {
            if (EditorApplication.isPlaying)
            {
                throw new PluginException(SceneToolErrors.PlayModeActive,
                    "Cannot instantiate Prefabs while in Play Mode. Use control_play_mode to stop playback first.");
            }

            var prefabPath = Payload.GetString(parameters, "prefab_path");
            if (string.IsNullOrEmpty(prefabPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "prefab_path is required");
            }

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"Prefab not found at path: {prefabPath}");
            }

            var parentPath = Payload.GetString(parameters, "parent_path");
            Transform parentTransform = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parentGo = GameObjectResolver.Resolve(parentPath);
                if (parentGo == null)
                {
                    throw new PluginException(SceneToolErrors.ObjectNotFound,
                        $"Parent GameObject not found: {parentPath}");
                }

                parentTransform = parentGo.transform;
            }

            var undoGroup = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("instantiate_prefab");

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
            Undo.RegisterCreatedObjectUndo(instance, "Instantiate Prefab");

            if (parentTransform != null)
            {
                Undo.SetTransformParent(instance.transform, parentTransform, true, "Set parent");
            }

            var positionToken = parameters["position"] as JObject;
            if (positionToken != null)
            {
                var x = positionToken["x"]?.Value<float>() ?? 0f;
                var y = positionToken["y"]?.Value<float>() ?? 0f;
                var z = positionToken["z"]?.Value<float>() ?? 0f;
                instance.transform.position = new Vector3(x, y, z);
            }

            var rotationToken = parameters["rotation"] as JObject;
            if (rotationToken != null)
            {
                var x = rotationToken["x"]?.Value<float>() ?? 0f;
                var y = rotationToken["y"]?.Value<float>() ?? 0f;
                var z = rotationToken["z"]?.Value<float>() ?? 0f;
                instance.transform.rotation = Quaternion.Euler(x, y, z);
            }

            var nameOverride = Payload.GetString(parameters, "name");
            if (!string.IsNullOrEmpty(nameOverride))
            {
                instance.name = nameOverride;
            }

            var siblingIndex = Payload.GetInt(parameters, "sibling_index");
            if (siblingIndex.HasValue)
            {
                instance.transform.SetSiblingIndex(siblingIndex.Value);
            }

            Undo.CollapseUndoOperations(undoGroup);

            EditorSceneManager.SaveOpenScenes();

            return new InstantiatePrefabPayload(
                GameObjectResolver.GetHierarchyPath(instance),
                instance.name,
                prefabPath,
                instance.GetInstanceID());
        }
    }
}
