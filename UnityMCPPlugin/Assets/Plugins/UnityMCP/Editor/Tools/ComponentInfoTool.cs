using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal static class ComponentInfoTool
    {
        internal static object Execute(JObject parameters)
        {
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

            var go = GameObjectResolver.Resolve(gameObjectPath);
            if (go == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"GameObject not found: {gameObjectPath}");
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

            var serializeResult = FieldSerializer.Serialize(component, fieldFilter, maxArrayElements);

            var result = new JObject
            {
                ["game_object_path"] = GameObjectResolver.GetHierarchyPath(go),
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
