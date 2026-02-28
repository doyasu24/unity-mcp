using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal static class ManageComponentTool
    {
        internal static object Execute(JObject parameters)
        {
            if (EditorApplication.isPlaying)
            {
                throw new PluginException(SceneToolErrors.PlayModeActive,
                    "Cannot modify components while in Play Mode. Use control_play_mode to stop playback first.");
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

            var go = GameObjectResolver.Resolve(gameObjectPath);
            if (go == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"GameObject not found: {gameObjectPath}");
            }

            object result;
            switch (action)
            {
                case "add":
                    result = ExecuteAdd(go, parameters);
                    break;
                case "update":
                    result = ExecuteUpdate(go, parameters);
                    break;
                case "remove":
                    result = ExecuteRemove(go, parameters);
                    break;
                case "move":
                    result = ExecuteMove(go, parameters);
                    break;
                default:
                    throw new PluginException("ERR_INVALID_PARAMS",
                        $"action must be add|update|remove|move, got: {action}");
            }

            EditorSceneManager.SaveOpenScenes();
            return result;
        }

        private static object ExecuteAdd(GameObject go, JObject parameters)
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

            // Phase 1: Pre-resolve references
            Dictionary<string, ResolvedRef> resolvedRefs = null;
            if (fields != null)
            {
                var tempComponent = go.AddComponent(type);
                try
                {
                    var tempSo = new SerializedObject(tempComponent);
                    resolvedRefs = ReferenceResolver.ResolveAll(fields, tempSo);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(tempComponent);
                }
            }

            // Phase 2: Apply
            var undoGroup = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("manage_component: add");

            var component = Undo.AddComponent(go, type);

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
                var result = FieldDeserializer.Apply(so, fields, resolvedRefs);
                so.ApplyModifiedProperties();
                RecordPrefabModifications(component);
                fieldsSet = result.FieldsSet;
                fieldsSkipped = result.FieldsSkipped;
            }

            Undo.CollapseUndoOperations(undoGroup);

            var finalComponents = go.GetComponents<Component>();
            var finalIdx = Array.IndexOf(finalComponents, component);

            return BuildResult("add", go, component, finalIdx, fieldsSet, fieldsSkipped);
        }

        private static object ExecuteUpdate(GameObject go, JObject parameters)
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

            // Phase 1: Pre-resolve references
            var so = new SerializedObject(component);
            var resolvedRefs = ReferenceResolver.ResolveAll(fields, so);

            // Phase 2: Apply
            var undoGroup = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("manage_component: update");

            var applyResult = FieldDeserializer.Apply(so, fields, resolvedRefs);
            so.ApplyModifiedProperties();
            RecordPrefabModifications(component);

            Undo.CollapseUndoOperations(undoGroup);

            return BuildResult("update", go, component, index.Value, applyResult.FieldsSet, applyResult.FieldsSkipped);
        }

        private static object ExecuteRemove(GameObject go, JObject parameters)
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
                ["game_object_path"] = GameObjectResolver.GetHierarchyPath(go),
                ["game_object_name"] = go.name,
                ["component_type"] = componentType,
                ["index"] = index.Value
            };

            Undo.DestroyObjectImmediate(component);

            return result;
        }

        private static object ExecuteMove(GameObject go, JObject parameters)
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
                ["game_object_path"] = GameObjectResolver.GetHierarchyPath(go),
                ["game_object_name"] = go.name,
                ["component_type"] = component.GetType().FullName,
                ["index"] = finalIdx,
                ["previous_index"] = previousIndex
            };
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

        private static void RecordPrefabModifications(Component component)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(component))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }
        }

        private static JObject BuildResult(
            string action,
            GameObject go,
            Component component,
            int index,
            List<string> fieldsSet,
            List<string> fieldsSkipped)
        {
            var result = new JObject
            {
                ["action"] = action,
                ["game_object_path"] = GameObjectResolver.GetHierarchyPath(go),
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
