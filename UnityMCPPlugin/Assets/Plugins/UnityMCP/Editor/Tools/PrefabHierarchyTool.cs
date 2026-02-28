using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal static class PrefabHierarchyTool
    {
        internal static object Execute(JObject parameters)
        {
            var prefabPath = Payload.GetString(parameters, "prefab_path");
            if (string.IsNullOrEmpty(prefabPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "prefab_path is required");
            }

            if (!prefabPath.EndsWith(".prefab"))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "prefab_path must end with .prefab");
            }

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null)
            {
                throw new PluginException(PrefabToolErrors.PrefabNotFound,
                    $"Prefab not found at path: {prefabPath}");
            }

            var gameObjectPath = Payload.GetString(parameters, "game_object_path");
            var maxDepth = Payload.GetInt(parameters, "max_depth") ?? SceneToolLimits.MaxDepthDefault;
            var maxGameObjects = Payload.GetInt(parameters, "max_game_objects") ?? SceneToolLimits.MaxGameObjectsDefault;

            GameObject startRoot;
            if (!string.IsNullOrEmpty(gameObjectPath))
            {
                startRoot = PrefabGameObjectResolver.Resolve(prefabAsset, gameObjectPath);
                if (startRoot == null)
                {
                    throw new PluginException(SceneToolErrors.ObjectNotFound,
                        $"GameObject not found in prefab: {gameObjectPath}");
                }
            }
            else
            {
                startRoot = prefabAsset;
            }

            var totalCount = 0;
            var truncated = false;

            var rootNode = BuildNode(prefabAsset, startRoot);

            totalCount++;

            if (maxDepth > 0)
            {
                var childArray = new JArray();
                var queue = new Queue<(GameObject go, JArray parentArray, int depth)>();
                for (var i = 0; i < startRoot.transform.childCount; i++)
                {
                    var child = startRoot.transform.GetChild(i).gameObject;
                    queue.Enqueue((child, childArray, 1));
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

                    var node = BuildNode(prefabAsset, go);

                    if (depth < maxDepth)
                    {
                        var nextChildArray = new JArray();
                        for (var i = 0; i < go.transform.childCount; i++)
                        {
                            var c = go.transform.GetChild(i).gameObject;
                            queue.Enqueue((c, nextChildArray, depth + 1));
                        }

                        node["children"] = nextChildArray;
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

                rootNode["children"] = childArray;
            }
            else if (startRoot.transform.childCount > 0)
            {
                rootNode["children"] = new JValue("...");
                truncated = true;
            }
            else
            {
                rootNode["children"] = new JArray();
            }

            var result = new JObject
            {
                ["prefab_path"] = prefabPath,
                ["prefab_name"] = prefabAsset.name,
                ["root"] = rootNode,
                ["total_game_objects"] = totalCount,
                ["truncated"] = truncated
            };

            return result;
        }

        private static JObject BuildNode(GameObject prefabRoot, GameObject go)
        {
            var node = new JObject
            {
                ["name"] = go.name,
                ["path"] = PrefabGameObjectResolver.GetRelativePath(prefabRoot, go),
                ["active"] = go.activeSelf
            };

            if (PrefabUtility.IsAnyPrefabInstanceRoot(go) && go != prefabRoot)
            {
                var nestedPrefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (!string.IsNullOrEmpty(nestedPrefabPath))
                {
                    node["nested_prefab_asset_path"] = nestedPrefabPath;
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
