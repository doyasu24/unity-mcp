using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal static class ManagePrefabComponentTool
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

            var action = Payload.GetString(parameters, "action");
            if (string.IsNullOrEmpty(action))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "action is required");
            }

            var gameObjectPath = Payload.GetString(parameters, "game_object_path");
            if (string.IsNullOrEmpty(gameObjectPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "game_object_path is required");
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var go = PrefabGameObjectResolver.Resolve(root, gameObjectPath);
                if (go == null)
                {
                    throw new PluginException(SceneToolErrors.ObjectNotFound,
                        $"GameObject not found in prefab: {gameObjectPath}");
                }

                var context = ToolContext.ForPrefab(root, prefabPath);

                object result;
                switch (action)
                {
                    case "add":
                        result = ExecuteAdd(go, parameters, root, prefabPath, context);
                        break;
                    case "update":
                        result = ExecuteUpdate(go, parameters, root, prefabPath, context);
                        break;
                    case "remove":
                        result = ExecuteRemove(go, parameters, root, prefabPath);
                        break;
                    case "move":
                        result = ExecuteMove(go, parameters, root, prefabPath);
                        break;
                    default:
                        throw new PluginException("ERR_INVALID_PARAMS",
                            $"action must be add|update|remove|move, got: {action}");
                }

                SavePrefab(root, prefabPath);
                return result;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static object ExecuteAdd(GameObject go, JObject parameters, GameObject prefabRoot, string prefabPath, ToolContext context)
        {
            var componentType = Payload.GetString(parameters, "component_type");
            if (string.IsNullOrEmpty(componentType))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "component_type is required for 'add' action");
            }

            var type = ComponentTypeResolver.Resolve(componentType);
            if (type == null)
            {
                throw new PluginException(SceneToolErrors.ComponentTypeNotFound,
                    $"Component type not found: {componentType}");
            }

            if (!typeof(Component).IsAssignableFrom(type))
            {
                throw new PluginException(SceneToolErrors.InvalidComponentType,
                    $"'{type.FullName}' does not inherit from Component");
            }

            var existingComponents = go.GetComponents<Component>();
            var insertIndex = Payload.GetInt(parameters, "index");

            if (insertIndex.HasValue)
            {
                if (insertIndex.Value == 0)
                {
                    throw new PluginException("ERR_INVALID_PARAMS",
                        "Cannot insert at index 0 (Transform position)");
                }

                if (insertIndex.Value > existingComponents.Length)
                {
                    throw new PluginException(SceneToolErrors.ComponentIndexOutOfRange,
                        $"index {insertIndex.Value} is out of range (1..{existingComponents.Length})");
                }
            }

            JObject fields = null;
            if (Payload.TryGetObject(parameters, "fields", out var fieldsObj))
            {
                fields = fieldsObj;
            }

            Dictionary<string, ResolvedRef> resolvedRefs = null;
            if (fields != null)
            {
                var tempComponent = go.AddComponent(type);
                try
                {
                    var tempSo = new SerializedObject(tempComponent);
                    resolvedRefs = ReferenceResolver.ResolveAll(fields, tempSo, context);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(tempComponent);
                }
            }

            var component = go.AddComponent(type);

            if (insertIndex.HasValue)
            {
                var currentComponents = go.GetComponents<Component>();
                var currentIdx = Array.IndexOf(currentComponents, component);
                while (currentIdx > insertIndex.Value)
                {
                    UnityEditorInternal.ComponentUtility.MoveComponentUp(component);
                    currentIdx--;
                }
            }

            var fieldsSet = new List<string>();
            var fieldsSkipped = new List<string>();

            if (fields != null && resolvedRefs != null)
            {
                var so = new SerializedObject(component);
                var applyResult = FieldDeserializer.Apply(so, fields, resolvedRefs);
                so.ApplyModifiedPropertiesWithoutUndo();
                fieldsSet = applyResult.FieldsSet;
                fieldsSkipped = applyResult.FieldsSkipped;
            }

            var finalComponents = go.GetComponents<Component>();
            var finalIdx = Array.IndexOf(finalComponents, component);

            return BuildResult("add", go, component, finalIdx, prefabRoot, prefabPath, fieldsSet, fieldsSkipped);
        }

        private static object ExecuteUpdate(GameObject go, JObject parameters, GameObject prefabRoot, string prefabPath, ToolContext context)
        {
            var index = Payload.GetInt(parameters, "index");
            if (!index.HasValue)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "index is required for 'update' action");
            }

            JObject fields;
            if (!Payload.TryGetObject(parameters, "fields", out fields))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "fields is required for 'update' action");
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

            var so = new SerializedObject(component);
            var resolvedRefs = ReferenceResolver.ResolveAll(fields, so, context);

            var applyResult = FieldDeserializer.Apply(so, fields, resolvedRefs);
            so.ApplyModifiedPropertiesWithoutUndo();

            return BuildResult("update", go, component, index.Value, prefabRoot, prefabPath, applyResult.FieldsSet, applyResult.FieldsSkipped);
        }

        private static object ExecuteRemove(GameObject go, JObject parameters, GameObject prefabRoot, string prefabPath)
        {
            var index = Payload.GetInt(parameters, "index");
            if (!index.HasValue)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "index is required for 'remove' action");
            }

            if (index.Value == 0)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "Cannot remove Transform (index 0)");
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

            CheckRequireComponentDependency(go, component, components);

            var componentType = component.GetType().FullName;
            var result = new JObject
            {
                ["action"] = "remove",
                ["prefab_path"] = prefabPath,
                ["game_object_path"] = PrefabGameObjectResolver.GetRelativePath(prefabRoot, go),
                ["game_object_name"] = go.name,
                ["component_type"] = componentType,
                ["index"] = index.Value
            };

            UnityEngine.Object.DestroyImmediate(component);

            return result;
        }

        private static object ExecuteMove(GameObject go, JObject parameters, GameObject prefabRoot, string prefabPath)
        {
            var index = Payload.GetInt(parameters, "index");
            if (!index.HasValue)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "index is required for 'move' action");
            }

            var newIndex = Payload.GetInt(parameters, "new_index");
            if (!newIndex.HasValue)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "new_index is required for 'move' action");
            }

            if (index.Value == 0 || newIndex.Value == 0)
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    "Cannot move Transform (index 0) or move to index 0");
            }

            var components = go.GetComponents<Component>();
            if (index.Value < 0 || index.Value >= components.Length)
            {
                throw new PluginException(SceneToolErrors.ComponentIndexOutOfRange,
                    $"index {index.Value} is out of range (0..{components.Length - 1})");
            }

            if (newIndex.Value < 0 || newIndex.Value >= components.Length)
            {
                throw new PluginException(SceneToolErrors.ComponentIndexOutOfRange,
                    $"new_index {newIndex.Value} is out of range (0..{components.Length - 1})");
            }

            var component = components[index.Value];
            if (component == null)
            {
                throw new PluginException(SceneToolErrors.MissingScript,
                    $"Component at index {index.Value} is a missing script");
            }

            var previousIndex = index.Value;

            if (index.Value != newIndex.Value)
            {
                if (index.Value < newIndex.Value)
                {
                    for (var i = index.Value; i < newIndex.Value; i++)
                    {
                        UnityEditorInternal.ComponentUtility.MoveComponentDown(component);
                    }
                }
                else
                {
                    for (var i = index.Value; i > newIndex.Value; i--)
                    {
                        UnityEditorInternal.ComponentUtility.MoveComponentUp(component);
                    }
                }
            }

            var finalComponents = go.GetComponents<Component>();
            var finalIdx = Array.IndexOf(finalComponents, component);

            return new JObject
            {
                ["action"] = "move",
                ["prefab_path"] = prefabPath,
                ["game_object_path"] = PrefabGameObjectResolver.GetRelativePath(prefabRoot, go),
                ["game_object_name"] = go.name,
                ["component_type"] = component.GetType().FullName,
                ["index"] = finalIdx,
                ["previous_index"] = previousIndex
            };
        }

        private static void SavePrefab(GameObject root, string prefabPath)
        {
            bool success;
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out success);
            if (!success)
            {
                throw new PluginException(PrefabToolErrors.PrefabSaveFailed,
                    $"Failed to save prefab at: {prefabPath}");
            }
        }

        private static void CheckRequireComponentDependency(GameObject go, Component target, Component[] components)
        {
            var targetType = target.GetType();
            foreach (var other in components)
            {
                if (other == null || other == target)
                {
                    continue;
                }

                var attrs = other.GetType().GetCustomAttributes(typeof(RequireComponent), true);
                foreach (RequireComponent req in attrs)
                {
                    if (req.m_Type0 == targetType || req.m_Type1 == targetType || req.m_Type2 == targetType)
                    {
                        throw new PluginException(SceneToolErrors.ComponentDependency,
                            $"Cannot remove {targetType.Name}: {other.GetType().Name} depends on it via [RequireComponent]");
                    }
                }
            }
        }

        private static JObject BuildResult(
            string action,
            GameObject go,
            Component component,
            int index,
            GameObject prefabRoot,
            string prefabPath,
            List<string> fieldsSet,
            List<string> fieldsSkipped)
        {
            var result = new JObject
            {
                ["action"] = action,
                ["prefab_path"] = prefabPath,
                ["game_object_path"] = PrefabGameObjectResolver.GetRelativePath(prefabRoot, go),
                ["game_object_name"] = go.name,
                ["component_type"] = component.GetType().FullName,
                ["index"] = index
            };

            if (fieldsSet != null)
            {
                result["fields_set"] = new JArray(fieldsSet.ToArray());
            }

            if (fieldsSkipped != null)
            {
                result["fields_skipped"] = new JArray(fieldsSkipped.ToArray());
            }

            return result;
        }
    }
}
