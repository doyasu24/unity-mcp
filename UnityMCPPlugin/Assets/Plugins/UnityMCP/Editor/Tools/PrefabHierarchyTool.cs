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
            var offset = Payload.GetInt(parameters, "offset") ?? 0;
            var componentFilter = ParseComponentFilter(parameters);

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

            var result = new JObject
            {
                ["prefab_path"] = prefabPath,
                ["prefab_name"] = prefabAsset.name
            };

            if (offset > 0)
            {
                ExecuteWithOffset(result, prefabAsset, startRoot, maxDepth, maxGameObjects, offset, componentFilter);
            }
            else
            {
                ExecuteTree(result, prefabAsset, startRoot, maxDepth, maxGameObjects, componentFilter);
            }

            return result;
        }

        private static void ExecuteTree(JObject result, GameObject prefabAsset, GameObject startRoot, int maxDepth, int maxGameObjects, HashSet<string> componentFilter)
        {
            var matchCount = 0;
            var truncated = false;

            var rootMatches = MatchesComponentFilter(startRoot, componentFilter);
            if (rootMatches) matchCount++;

            var rootNode = BuildNode(prefabAsset, startRoot, rootMatches || componentFilter == null);

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

                    var matches = MatchesComponentFilter(go, componentFilter);
                    if (matches)
                    {
                        if (matchCount >= maxGameObjects)
                        {
                            truncated = true;
                            break;
                        }

                        matchCount++;
                    }

                    var node = BuildNode(prefabAsset, go, matches || componentFilter == null);

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

            result["root"] = rootNode;
            result["total_game_objects"] = matchCount;
            result["truncated"] = truncated;
        }

        private static void ExecuteWithOffset(JObject result, GameObject prefabAsset, GameObject startRoot, int maxDepth, int maxGameObjects, int offset, HashSet<string> componentFilter)
        {
            var globalIndex = 0;
            var emittedCount = 0;
            var truncated = false;
            var flatArray = new JArray();

            var queue = new Queue<(GameObject go, int depth)>();
            queue.Enqueue((startRoot, 0));

            while (queue.Count > 0)
            {
                var (go, depth) = queue.Dequeue();

                if (MatchesComponentFilter(go, componentFilter))
                {
                    if (globalIndex >= offset && emittedCount < maxGameObjects)
                    {
                        flatArray.Add(BuildNode(prefabAsset, go, true));
                        emittedCount++;
                    }
                    else if (emittedCount >= maxGameObjects)
                    {
                        truncated = true;
                        break;
                    }

                    globalIndex++;
                }

                if (depth < maxDepth)
                {
                    for (var i = 0; i < go.transform.childCount; i++)
                    {
                        queue.Enqueue((go.transform.GetChild(i).gameObject, depth + 1));
                    }
                }
                else if (go.transform.childCount > 0)
                {
                    truncated = true;
                }
            }

            if (!truncated && queue.Count > 0)
            {
                truncated = true;
            }

            result["game_objects"] = flatArray;
            result["total_game_objects"] = emittedCount;
            result["truncated"] = truncated;

            if (truncated)
            {
                result["next_offset"] = offset + emittedCount;
            }
        }

        private static JObject BuildNode(GameObject prefabRoot, GameObject go, bool includeComponents)
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

            if (includeComponents)
            {
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
            }

            return node;
        }

        private static HashSet<string> ParseComponentFilter(JObject parameters)
        {
            if (parameters?["component_filter"] is not JArray arr || arr.Count == 0) return null;
            var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var token in arr)
            {
                var val = token?.Value<string>();
                if (!string.IsNullOrEmpty(val)) set.Add(val);
            }

            return set.Count > 0 ? set : null;
        }

        private static bool MatchesComponentFilter(GameObject go, HashSet<string> filter)
        {
            if (filter == null) return true;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var type = c.GetType();
                if (filter.Contains(type.Name) || filter.Contains(type.FullName)) return true;
            }

            return false;
        }
    }
}
