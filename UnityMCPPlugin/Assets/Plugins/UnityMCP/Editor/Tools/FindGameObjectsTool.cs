using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMcpPlugin.Tools
{
    internal sealed class FindGameObjectsTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.FindSceneGameObjects;

        public override object Execute(JObject parameters)
        {
            var nameFilter = Payload.GetString(parameters, "name");
            var tagFilter = Payload.GetString(parameters, "tag");
            var componentTypeFilter = Payload.GetString(parameters, "component_type");
            var rootPath = Payload.GetString(parameters, "root_path");
            var layerFilter = Payload.GetInt(parameters, "layer");
            var activeFilter = ManageGameObjectTool.GetBool(parameters, "active");
            var maxResults = Payload.GetInt(parameters, "max_results") ?? FindSceneGameObjectsLimits.MaxResultsDefault;
            var offset = Payload.GetInt(parameters, "offset") ?? 0;

            if (maxResults > FindSceneGameObjectsLimits.MaxResultsMax)
            {
                maxResults = FindSceneGameObjectsLimits.MaxResultsMax;
            }

            if (string.IsNullOrEmpty(nameFilter) && string.IsNullOrEmpty(tagFilter) && string.IsNullOrEmpty(componentTypeFilter) && !layerFilter.HasValue)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "at least one filter (name, tag, component_type, or layer) is required");
            }

            if (!string.IsNullOrEmpty(nameFilter))
            {
                try
                {
                    Regex.IsMatch("", nameFilter);
                }
                catch (ArgumentException)
                {
                    throw new PluginException("ERR_INVALID_PARAMS", $"Invalid regex pattern: {nameFilter}");
                }
            }

            Type componentType = null;
            if (!string.IsNullOrEmpty(componentTypeFilter))
            {
                componentType = ComponentTypeResolver.Resolve(componentTypeFilter);
                if (componentType == null)
                {
                    throw new PluginException(SceneToolErrors.ComponentTypeNotFound, $"Component type not found: {componentTypeFilter}");
                }
            }

            var roots = GetSearchRoots(rootPath);
            var results = new List<FoundGameObject>();
            var skipped = 0;
            var totalCount = 0;

            foreach (var root in roots)
            {
                CollectMatching(root.transform, nameFilter, tagFilter, componentType, layerFilter, activeFilter, maxResults, offset, results, ref skipped, ref totalCount);
            }

            var truncated = totalCount > offset + results.Count;
            int? nextOffset = truncated ? offset + results.Count : null;
            return new FindSceneGameObjectsPayload(results, results.Count, totalCount, truncated, nextOffset);
        }

        private static GameObject[] GetSearchRoots(string rootPath)
        {
            if (!string.IsNullOrEmpty(rootPath))
            {
                var rootGo = GameObjectResolver.Resolve(rootPath);
                if (rootGo == null)
                {
                    throw new PluginException(SceneToolErrors.ObjectNotFound, $"Root GameObject not found: {rootPath}");
                }

                return new[] { rootGo };
            }

            return SceneManager.GetActiveScene().GetRootGameObjects();
        }

        private static void CollectMatching(
            Transform current,
            string nameFilter,
            string tagFilter,
            Type componentType,
            int? layerFilter,
            bool? activeFilter,
            int maxResults,
            int offset,
            List<FoundGameObject> results,
            ref int skipped,
            ref int totalCount)
        {
            var go = current.gameObject;
            if (Matches(go, nameFilter, tagFilter, componentType, layerFilter, activeFilter))
            {
                totalCount++;
                if (skipped < offset)
                {
                    skipped++;
                }
                else if (results.Count < maxResults)
                {
                    results.Add(BuildFoundGameObject(go));
                }
            }

            for (var i = 0; i < current.childCount; i++)
            {
                CollectMatching(current.GetChild(i), nameFilter, tagFilter, componentType, layerFilter, activeFilter, maxResults, offset, results, ref skipped, ref totalCount);
            }
        }

        private static bool Matches(GameObject go, string nameFilter, string tagFilter, Type componentType, int? layerFilter, bool? activeFilter)
        {
            if (!string.IsNullOrEmpty(nameFilter))
            {
                if (!Regex.IsMatch(go.name, nameFilter, RegexOptions.IgnoreCase))
                {
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(tagFilter))
            {
                if (!go.CompareTag(tagFilter))
                {
                    return false;
                }
            }

            if (componentType != null)
            {
                if (go.GetComponent(componentType) == null)
                {
                    return false;
                }
            }

            if (layerFilter.HasValue && go.layer != layerFilter.Value)
            {
                return false;
            }

            if (activeFilter.HasValue && go.activeInHierarchy != activeFilter.Value)
            {
                return false;
            }

            return true;
        }

        private static FoundGameObject BuildFoundGameObject(GameObject go)
        {
            var path = GameObjectResolver.GetHierarchyPath(go);
            var components = go.GetComponents<Component>();
            var componentNames = new List<string>(components.Length);
            foreach (var c in components)
            {
                componentNames.Add(c != null ? c.GetType().Name : "Missing");
            }

            return new FoundGameObject(
                go.name,
                path,
                go.tag,
                go.layer,
                go.activeInHierarchy,
                componentNames);
        }
    }
}
