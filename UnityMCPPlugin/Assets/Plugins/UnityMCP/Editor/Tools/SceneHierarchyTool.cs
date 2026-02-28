using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMcpPlugin.Tools
{
    internal static class SceneHierarchyTool
    {
        internal static object Execute(JObject parameters)
        {
            var rootPath = Payload.GetString(parameters, "root_path");
            var maxDepth = Payload.GetInt(parameters, "max_depth") ?? SceneToolLimits.MaxDepthDefault;
            var maxGameObjects = Payload.GetInt(parameters, "max_game_objects") ?? SceneToolLimits.MaxGameObjectsDefault;

            var scene = SceneManager.GetActiveScene();
            var result = new JObject
            {
                ["scene_name"] = scene.name,
                ["scene_path"] = scene.path
            };

            List<GameObject> startRoots;
            if (!string.IsNullOrEmpty(rootPath))
            {
                var rootGo = GameObjectResolver.Resolve(rootPath);
                if (rootGo == null)
                {
                    throw new PluginException(SceneToolErrors.ObjectNotFound,
                        $"GameObject not found: {rootPath}");
                }

                startRoots = new List<GameObject> { rootGo };
            }
            else
            {
                var sceneRoots = scene.GetRootGameObjects();
                startRoots = new List<GameObject>(sceneRoots);
            }

            var totalCount = 0;
            var truncated = false;
            var rootArray = new JArray();

            var queue = new Queue<(GameObject go, JArray parentArray, int depth)>();
            foreach (var root in startRoots)
            {
                queue.Enqueue((root, rootArray, 0));
            }

            while (queue.Count > 0)
            {
                var (go, parentArray, depth) = queue.Dequeue();

                if (totalCount >= maxGameObjects)
                {
                    truncated = true;
                    break;
                }

                totalCount++;

                var node = BuildNode(go);

                if (depth < maxDepth)
                {
                    var childArray = new JArray();
                    for (var i = 0; i < go.transform.childCount; i++)
                    {
                        var child = go.transform.GetChild(i).gameObject;
                        queue.Enqueue((child, childArray, depth + 1));
                    }

                    node["children"] = childArray;
                }
                else if (go.transform.childCount > 0)
                {
                    node["children"] = new JValue("...");
                    truncated = true;
                }
                else
                {
                    node["children"] = new JArray();
                }

                parentArray.Add(node);
            }

            result["root_game_objects"] = rootArray;
            result["total_game_objects"] = totalCount;
            result["truncated"] = truncated;

            return result;
        }

        private static JObject BuildNode(GameObject go)
        {
            var node = new JObject
            {
                ["name"] = go.name,
                ["path"] = GameObjectResolver.GetHierarchyPath(go),
                ["active"] = go.activeSelf
            };

            if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
            {
                var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (!string.IsNullOrEmpty(prefabPath))
                {
                    node["prefab_asset_path"] = prefabPath;
                }
            }

            var components = go.GetComponents<Component>();
            var compArray = new JArray();
            foreach (var c in components)
            {
                if (c == null)
                {
                    compArray.Add(JValue.CreateNull());
                }
                else
                {
                    compArray.Add(new JValue(c.GetType().FullName));
                }
            }

            node["components"] = compArray;

            return node;
        }
    }
}
