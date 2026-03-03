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
            var offset = Payload.GetInt(parameters, "offset") ?? 0;
            var componentFilter = ParseComponentFilter(parameters);

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

            if (offset > 0)
            {
                ExecuteWithOffset(result, startRoots, maxDepth, maxGameObjects, offset, componentFilter);
            }
            else
            {
                ExecuteTree(result, startRoots, maxDepth, maxGameObjects, componentFilter);
            }

            return result;
        }

        private static void ExecuteTree(JObject result, List<GameObject> startRoots, int maxDepth, int maxGameObjects, HashSet<string> componentFilter)
        {
            var matchCount = 0;
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

                var node = BuildNode(go, matches || componentFilter == null);

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
            result["total_game_objects"] = matchCount;
            result["truncated"] = truncated;
        }

        private static void ExecuteWithOffset(JObject result, List<GameObject> startRoots, int maxDepth, int maxGameObjects, int offset, HashSet<string> componentFilter)
        {
            var globalIndex = 0;
            var emittedCount = 0;
            var truncated = false;
            var flatArray = new JArray();

            var queue = new Queue<(GameObject go, int depth)>();
            foreach (var root in startRoots)
            {
                queue.Enqueue((root, 0));
            }

            while (queue.Count > 0)
            {
                var (go, depth) = queue.Dequeue();

                if (MatchesComponentFilter(go, componentFilter))
                {
                    if (globalIndex >= offset && emittedCount < maxGameObjects)
                    {
                        flatArray.Add(BuildNode(go, true));
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

        private static JObject BuildNode(GameObject go, bool includeComponents)
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
