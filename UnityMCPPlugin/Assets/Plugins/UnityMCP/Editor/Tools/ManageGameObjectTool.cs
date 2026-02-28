using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal static class ManageGameObjectTool
    {
        internal static object Execute(JObject parameters)
        {
            if (EditorApplication.isPlaying)
            {
                throw new PluginException(SceneToolErrors.PlayModeActive,
                    "Cannot modify GameObjects while in Play Mode. Use control_play_mode to stop playback first.");
            }

            var action = Payload.GetString(parameters, "action");
            if (string.IsNullOrEmpty(action))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "action is required");
            }

            object result;
            switch (action)
            {
                case "create":
                    result = ExecuteCreate(parameters);
                    break;
                case "update":
                    result = ExecuteUpdate(parameters);
                    break;
                case "delete":
                    result = ExecuteDelete(parameters);
                    break;
                case "reparent":
                    result = ExecuteReparent(parameters);
                    break;
                default:
                    throw new PluginException("ERR_INVALID_PARAMS",
                        $"action must be create|update|delete|reparent, got: {action}");
            }

            EditorSceneManager.SaveOpenScenes();
            return result;
        }

        private static object ExecuteCreate(JObject parameters)
        {
            var name = Payload.GetString(parameters, "name");
            if (string.IsNullOrEmpty(name))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "name is required for 'create' action");
            }

            var parentPath = Payload.GetString(parameters, "parent_path");
            Transform parentTransform = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parentGo = GameObjectResolver.Resolve(parentPath);
                if (parentGo == null)
                {
                    throw new PluginException(SceneToolErrors.ObjectNotFound,
                        $"Parent GameObject not found: {parentPath}");
                }

                parentTransform = parentGo.transform;
            }

            var primitiveType = Payload.GetString(parameters, "primitive_type");

            var undoGroup = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("manage_game_object: create");

            GameObject go;
            if (!string.IsNullOrEmpty(primitiveType))
            {
                var pt = ParsePrimitiveType(primitiveType);
                go = GameObject.CreatePrimitive(pt);
                go.name = name;
                Undo.RegisterCreatedObjectUndo(go, "Create primitive GameObject");
            }
            else
            {
                go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Create empty GameObject");
            }

            if (parentTransform != null)
            {
                Undo.SetTransformParent(go.transform, parentTransform, "Set parent");
            }

            ApplyOptionalProperties(go, parameters);

            var siblingIndex = Payload.GetInt(parameters, "sibling_index");
            if (siblingIndex.HasValue)
            {
                go.transform.SetSiblingIndex(siblingIndex.Value);
            }

            Undo.CollapseUndoOperations(undoGroup);

            var result = new JObject
            {
                ["action"] = "create",
                ["game_object_path"] = GameObjectResolver.GetHierarchyPath(go),
                ["game_object_name"] = go.name,
            };

            if (parentTransform != null)
            {
                result["parent_path"] = GameObjectResolver.GetHierarchyPath(parentTransform.gameObject);
            }

            if (!string.IsNullOrEmpty(primitiveType))
            {
                result["primitive_type"] = primitiveType;
            }

            return result;
        }

        private static object ExecuteUpdate(JObject parameters)
        {
            var gameObjectPath = Payload.GetString(parameters, "game_object_path");
            if (string.IsNullOrEmpty(gameObjectPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "game_object_path is required for 'update' action");
            }

            var go = GameObjectResolver.Resolve(gameObjectPath);
            if (go == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"GameObject not found: {gameObjectPath}");
            }

            var undoGroup = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("manage_game_object: update");

            Undo.RecordObject(go, "Update GameObject");

            var propertiesSet = new List<string>();
            var previousPath = gameObjectPath;

            var newName = Payload.GetString(parameters, "name");
            if (!string.IsNullOrEmpty(newName))
            {
                go.name = newName;
                propertiesSet.Add("name");
            }

            var tag = Payload.GetString(parameters, "tag");
            if (tag != null)
            {
                ValidateTag(tag);
                go.tag = tag;
                propertiesSet.Add("tag");
            }

            var layer = Payload.GetInt(parameters, "layer");
            if (layer.HasValue)
            {
                go.layer = layer.Value;
                propertiesSet.Add("layer");
            }

            var active = GetBool(parameters, "active");
            if (active.HasValue)
            {
                go.SetActive(active.Value);
                propertiesSet.Add("active");
            }

            Undo.CollapseUndoOperations(undoGroup);

            var result = new JObject
            {
                ["action"] = "update",
                ["game_object_path"] = GameObjectResolver.GetHierarchyPath(go),
                ["game_object_name"] = go.name,
                ["previous_path"] = previousPath,
                ["properties_set"] = new JArray(propertiesSet.ToArray()),
            };

            return result;
        }

        private static object ExecuteDelete(JObject parameters)
        {
            var gameObjectPath = Payload.GetString(parameters, "game_object_path");
            if (string.IsNullOrEmpty(gameObjectPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "game_object_path is required for 'delete' action");
            }

            var go = GameObjectResolver.Resolve(gameObjectPath);
            if (go == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"GameObject not found: {gameObjectPath}");
            }

            var childrenDeleted = CountDescendants(go.transform);
            var goName = go.name;
            var goPath = GameObjectResolver.GetHierarchyPath(go);

            Undo.DestroyObjectImmediate(go);

            return new JObject
            {
                ["action"] = "delete",
                ["game_object_path"] = goPath,
                ["game_object_name"] = goName,
                ["children_deleted"] = childrenDeleted,
            };
        }

        private static object ExecuteReparent(JObject parameters)
        {
            var gameObjectPath = Payload.GetString(parameters, "game_object_path");
            if (string.IsNullOrEmpty(gameObjectPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "game_object_path is required for 'reparent' action");
            }

            var go = GameObjectResolver.Resolve(gameObjectPath);
            if (go == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"GameObject not found: {gameObjectPath}");
            }

            var previousParentPath = go.transform.parent != null
                ? GameObjectResolver.GetHierarchyPath(go.transform.parent.gameObject)
                : null;

            var parentPath = Payload.GetString(parameters, "parent_path");
            Transform newParent = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parentGo = GameObjectResolver.Resolve(parentPath);
                if (parentGo == null)
                {
                    throw new PluginException(SceneToolErrors.ObjectNotFound,
                        $"New parent GameObject not found: {parentPath}");
                }

                if (parentGo == go)
                {
                    throw new PluginException(SceneToolErrors.CircularHierarchy,
                        "Cannot reparent a GameObject to itself");
                }

                if (parentGo.transform.IsChildOf(go.transform))
                {
                    throw new PluginException(SceneToolErrors.CircularHierarchy,
                        "Cannot reparent a GameObject to one of its descendants");
                }

                newParent = parentGo.transform;
            }

            var worldPositionStays = GetBool(parameters, "world_position_stays") ?? true;

            var undoGroup = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("manage_game_object: reparent");

            Undo.SetTransformParent(go.transform, newParent, worldPositionStays, "Reparent GameObject");

            var siblingIndex = Payload.GetInt(parameters, "sibling_index");
            if (siblingIndex.HasValue)
            {
                go.transform.SetSiblingIndex(siblingIndex.Value);
            }

            Undo.CollapseUndoOperations(undoGroup);

            var result = new JObject
            {
                ["action"] = "reparent",
                ["game_object_path"] = GameObjectResolver.GetHierarchyPath(go),
                ["game_object_name"] = go.name,
                ["previous_parent_path"] = previousParentPath != null ? (JToken)previousParentPath : JValue.CreateNull(),
                ["new_parent_path"] = newParent != null ? (JToken)GameObjectResolver.GetHierarchyPath(newParent.gameObject) : JValue.CreateNull(),
                ["world_position_stays"] = worldPositionStays,
            };

            if (siblingIndex.HasValue)
            {
                result["sibling_index"] = go.transform.GetSiblingIndex();
            }

            return result;
        }

        private static void ApplyOptionalProperties(GameObject go, JObject parameters)
        {
            var tag = Payload.GetString(parameters, "tag");
            if (tag != null)
            {
                ValidateTag(tag);
                go.tag = tag;
            }

            var layer = Payload.GetInt(parameters, "layer");
            if (layer.HasValue)
            {
                go.layer = layer.Value;
            }

            var active = GetBool(parameters, "active");
            if (active.HasValue)
            {
                go.SetActive(active.Value);
            }
        }

        private static void ValidateTag(string tag)
        {
            try
            {
                // Unity throws ArgumentException for undefined tags
                GameObject.FindWithTag(tag);
            }
            catch (System.Exception)
            {
                throw new PluginException(SceneToolErrors.InvalidTag,
                    $"Tag is not defined in the TagManager: {tag}");
            }
        }

        private static PrimitiveType ParsePrimitiveType(string value)
        {
            switch (value)
            {
                case "Cube": return PrimitiveType.Cube;
                case "Sphere": return PrimitiveType.Sphere;
                case "Capsule": return PrimitiveType.Capsule;
                case "Cylinder": return PrimitiveType.Cylinder;
                case "Plane": return PrimitiveType.Plane;
                case "Quad": return PrimitiveType.Quad;
                default:
                    throw new PluginException("ERR_INVALID_PARAMS",
                        $"primitive_type must be Cube|Sphere|Capsule|Cylinder|Plane|Quad, got: {value}");
            }
        }

        internal static int CountDescendants(Transform t)
        {
            var count = 0;
            for (var i = 0; i < t.childCount; i++)
            {
                count++;
                count += CountDescendants(t.GetChild(i));
            }

            return count;
        }

        internal static bool? GetBool(JObject parameters, string key)
        {
            var token = parameters[key];
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            return null;
        }
    }
}
