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

            var go = GameObjectResolver.Resolve(gameObjectPath);
            if (go == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"GameObject not found: {gameObjectPath}");
            }

            var components = go.GetComponents<Component>();
            var index = Payload.GetInt(parameters, "index");

            // index 省略時はコンポーネント一覧を返す（フィールド値の展開なし）
            if (!index.HasValue)
            {
                return BuildComponentListing(go, components);
            }

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

        /// <summary>
        /// index 省略時に呼ばれる軽量なコンポーネント一覧レスポンスを構築する。
        /// </summary>
        internal static JObject BuildComponentListing(GameObject go, Component[] components)
        {
            var listing = new JArray();
            for (var i = 0; i < components.Length; i++)
            {
                var c = components[i];
                var entry = new JObject
                {
                    ["index"] = i,
                    ["component_type"] = c != null ? c.GetType().FullName : null,
                };
                if (c == null)
                {
                    entry["is_missing_script"] = true;
                }

                listing.Add(entry);
            }

            return new JObject
            {
                ["game_object_path"] = GameObjectResolver.GetHierarchyPath(go),
                ["game_object_name"] = go.name,
                ["mode"] = "list",
                ["components"] = listing,
                ["count"] = components.Length,
            };
        }
    }
}
