using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMcpPlugin.Tools
{
    internal sealed class SceneHierarchyTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.GetSceneHierarchy;

        public override object Execute(JObject parameters)
        {
            var rootPath = Payload.GetString(parameters, "root_path");
            var scenePath = Payload.GetString(parameters, "scene_path");
            var maxDepth = Payload.GetInt(parameters, "max_depth") ?? SceneToolLimits.MaxDepthDefault;
            var maxGameObjects = Payload.GetInt(parameters, "max_game_objects") ?? SceneToolLimits.MaxGameObjectsDefault;
            var offset = Payload.GetInt(parameters, "offset") ?? 0;
            var componentFilter = ParseComponentFilter(parameters);

            var activeScene = SceneManager.GetActiveScene();
            var result = new JObject();

            // 対象シーンの決定
            var targetScenes = ResolveTargetScenes(scenePath, rootPath, activeScene, result, out var resolvedRootGo);
            // フィルタ指定時は対象シーンのみ出力、未指定時は全ロードシーンのメタデータを含める
            var isFiltered = !string.IsNullOrEmpty(scenePath) || !string.IsNullOrEmpty(rootPath);

            if (offset > 0)
            {
                ExecuteMultiSceneFlat(result, targetScenes, activeScene, resolvedRootGo, maxDepth, maxGameObjects, offset, componentFilter, isFiltered);
            }
            else
            {
                ExecuteMultiSceneTree(result, targetScenes, activeScene, resolvedRootGo, maxDepth, maxGameObjects, componentFilter, isFiltered);
            }

            return result;
        }

        /// <summary>
        /// 対象シーンを決定する。scene_path 指定時はそのシーンのみ、root_path 指定時は GO の所属シーンのみ、
        /// いずれも未指定なら全ロードシーンを返す。
        /// </summary>
        private static List<Scene> ResolveTargetScenes(string scenePath, string rootPath, Scene activeScene, JObject result, out GameObject resolvedRootGo)
        {
            var targetScenes = new List<Scene>();
            resolvedRootGo = null;

            if (!string.IsNullOrEmpty(scenePath))
            {
                var found = false;
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.path == scenePath)
                    {
                        if (!s.isLoaded)
                        {
                            throw new PluginException(SceneToolErrors.ObjectNotFound,
                                $"Scene is not loaded: {scenePath}");
                        }

                        targetScenes.Add(s);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    throw new PluginException(SceneToolErrors.ObjectNotFound,
                        $"Scene not found among loaded scenes: {scenePath}");
                }
            }
            else if (!string.IsNullOrEmpty(rootPath))
            {
                resolvedRootGo = GameObjectResolver.Resolve(rootPath);
                if (resolvedRootGo == null)
                {
                    throw new PluginException(SceneToolErrors.ObjectNotFound,
                        $"GameObject not found: {rootPath}");
                }

                targetScenes.Add(resolvedRootGo.scene);

                // 曖昧性チェック: root_path の先頭セグメントが複数シーンに存在するか
                CheckAmbiguousRootPath(rootPath, resolvedRootGo, result);
            }
            else
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.isLoaded)
                    {
                        targetScenes.Add(s);
                    }
                }
            }

            return targetScenes;
        }

        /// <summary>
        /// root_path の先頭セグメントが複数シーンに存在する場合、レスポンスに警告情報を追加する。
        /// </summary>
        private static void CheckAmbiguousRootPath(string rootPath, GameObject resolvedGo, JObject result)
        {
            var normalized = rootPath.TrimStart('/');
            if (string.IsNullOrEmpty(normalized)) return;

            var rootName = normalized.Split('/')[0];
            var resolvedScene = resolvedGo.scene;
            var candidateScenes = new List<string>();

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var go in scene.GetRootGameObjects())
                {
                    if (go.name == rootName && scene.path != resolvedScene.path)
                    {
                        candidateScenes.Add(scene.path);
                        break;
                    }
                }
            }

            if (candidateScenes.Count > 0)
            {
                result["ambiguous_root_path"] = true;
                var arr = new JArray { resolvedScene.path };
                foreach (var p in candidateScenes) arr.Add(p);
                result["ambiguous_candidate_scenes"] = arr;
            }
        }

        private static void ExecuteMultiSceneTree(JObject result, List<Scene> targetScenes, Scene activeScene,
            GameObject resolvedRootGo, int maxDepth, int maxGameObjects, HashSet<string> componentFilter, bool isFiltered)
        {
            var scenesArray = new JArray();
            var totalCount = 0;
            var truncated = false;

            // フィルタ時は対象シーンのみ、未フィルタ時は全ロードシーン（メタデータ含む）
            var outputScenes = isFiltered ? targetScenes : GetAllLoadedScenes();

            foreach (var scene in outputScenes)
            {
                var sceneEntry = new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["is_active"] = scene.path == activeScene.path
                };

                var isTarget = IsTargetScene(scene, targetScenes);

                if (isTarget && !truncated)
                {
                    List<GameObject> roots;
                    if (resolvedRootGo != null)
                    {
                        roots = resolvedRootGo.scene.path == scene.path
                            ? new List<GameObject> { resolvedRootGo }
                            : new List<GameObject>();
                    }
                    else
                    {
                        roots = new List<GameObject>(scene.GetRootGameObjects());
                    }

                    var rootArray = new JArray();
                    var queue = new Queue<(GameObject go, JArray parentArray, int depth)>();
                    foreach (var root in roots)
                    {
                        queue.Enqueue((root, rootArray, 0));
                    }

                    while (queue.Count > 0)
                    {
                        var (go, parentArray, depth) = queue.Dequeue();

                        var matches = MatchesComponentFilter(go, componentFilter);
                        if (matches)
                        {
                            if (totalCount >= maxGameObjects)
                            {
                                truncated = true;
                                break;
                            }

                            totalCount++;
                        }

                        var node = BuildNode(go, matches || componentFilter == null);

                        if (depth < maxDepth)
                        {
                            var childArray = new JArray();
                            for (var ci = 0; ci < go.transform.childCount; ci++)
                            {
                                var child = go.transform.GetChild(ci).gameObject;
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

                    sceneEntry["root_game_objects"] = rootArray;
                }
                else
                {
                    sceneEntry["root_game_objects"] = new JArray();
                }

                scenesArray.Add(sceneEntry);
            }

            result["scenes"] = scenesArray;
            result["total_game_objects"] = totalCount;
            result["truncated"] = truncated;
        }

        private static void ExecuteMultiSceneFlat(JObject result, List<Scene> targetScenes, Scene activeScene,
            GameObject resolvedRootGo, int maxDepth, int maxGameObjects, int offset, HashSet<string> componentFilter, bool isFiltered)
        {
            var outputScenes = isFiltered ? targetScenes : GetAllLoadedScenes();
            var globalIndex = 0;
            var emittedCount = 0;
            var budgetExhausted = false;
            var depthTruncated = false;

            // シーンごとの flat GO を格納
            var sceneGameObjects = new Dictionary<string, JArray>();
            foreach (var scene in outputScenes)
            {
                sceneGameObjects[scene.path] = new JArray();
            }

            foreach (var scene in targetScenes)
            {
                if (budgetExhausted) break;

                List<GameObject> roots;
                if (resolvedRootGo != null)
                {
                    roots = resolvedRootGo.scene.path == scene.path
                        ? new List<GameObject> { resolvedRootGo }
                        : new List<GameObject>();
                }
                else
                {
                    roots = new List<GameObject>(scene.GetRootGameObjects());
                }

                var queue = new Queue<(GameObject go, int depth)>();
                foreach (var root in roots)
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
                            sceneGameObjects[scene.path].Add(BuildNode(go, true));
                            emittedCount++;
                        }
                        else if (emittedCount >= maxGameObjects)
                        {
                            budgetExhausted = true;
                            break;
                        }

                        globalIndex++;
                    }

                    if (depth < maxDepth)
                    {
                        for (var ci = 0; ci < go.transform.childCount; ci++)
                        {
                            queue.Enqueue((go.transform.GetChild(ci).gameObject, depth + 1));
                        }
                    }
                    else if (go.transform.childCount > 0)
                    {
                        depthTruncated = true;
                    }
                }
            }

            var truncated = budgetExhausted || depthTruncated;

            var scenesArray = new JArray();
            foreach (var scene in outputScenes)
            {
                var sceneEntry = new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["is_active"] = scene.path == activeScene.path,
                    ["game_objects"] = sceneGameObjects.TryGetValue(scene.path, out var goArray) ? goArray : new JArray()
                };

                scenesArray.Add(sceneEntry);
            }

            result["scenes"] = scenesArray;
            result["total_game_objects"] = emittedCount;
            result["truncated"] = truncated;

            if (truncated)
            {
                result["next_offset"] = offset + emittedCount;
            }
        }

        private static List<Scene> GetAllLoadedScenes()
        {
            var scenes = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) scenes.Add(s);
            }

            return scenes;
        }

        private static bool IsTargetScene(Scene scene, List<Scene> targetScenes)
        {
            foreach (var t in targetScenes)
            {
                if (t.path == scene.path) return true;
            }

            return false;
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
