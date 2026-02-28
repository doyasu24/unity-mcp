using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal static class PrefabComponentInfoTool
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
            if (string.IsNullOrEmpty(gameObjectPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "game_object_path is required");
            }

            var index = Payload.GetInt(parameters, "index");
            if (!index.HasValue)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "index is required");
            }

            var maxArrayElements = Payload.GetInt(parameters, "max_array_elements") ?? SceneToolLimits.MaxArrayElementsDefault;

            HashSet<string> fieldFilter = null;
            if (parameters["fields"] is JArray fieldsArray && fieldsArray.Count > 0)
            {
                fieldFilter = new HashSet<string>();
                foreach (var item in fieldsArray)
                {
                    var name = item?.Value<string>();
                    if (!string.IsNullOrEmpty(name))
                    {
                        fieldFilter.Add(name);
                    }
                }
            }

            var go = PrefabGameObjectResolver.Resolve(prefabAsset, gameObjectPath);
            if (go == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"GameObject not found in prefab: {gameObjectPath}");
            }

            var components = go.GetComponents<Component>();
            if (index.Value < 0 || index.Value >= components.Length)
            {
                throw new PluginException(SceneToolErrors.ComponentIndexOutOfRange,
                    $"index {index.Value} is out of range (0..{components.Length - 1})");
            }

            var component = components[index.Value];
            if (component == null)
            {
                throw new PluginException(SceneToolErrors.MissingScript,
                    $"Component at index {index.Value} is a missing script");
            }

            var context = ToolContext.ForPrefab(prefabAsset, prefabPath);
            var serializeResult = FieldSerializer.Serialize(component, fieldFilter, maxArrayElements, context);

            var result = new JObject
            {
                ["prefab_path"] = prefabPath,
                ["game_object_path"] = PrefabGameObjectResolver.GetRelativePath(prefabAsset, go),
                ["game_object_name"] = go.name,
                ["index"] = index.Value,
                ["component_type"] = component.GetType().FullName,
                ["fields"] = serializeResult.Fields
            };

            if (serializeResult.FieldsTruncated)
            {
                result["_fields_truncated"] = true;
            }

            return result;
        }
    }
}
