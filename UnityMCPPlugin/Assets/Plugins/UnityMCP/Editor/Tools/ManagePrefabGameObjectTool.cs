using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal static class ManagePrefabGameObjectTool
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

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                object result;
                switch (action)
                {
                    case "create":
                        result = ExecuteCreate(parameters, root, prefabPath);
                        break;
                    case "update":
                        result = ExecuteUpdate(parameters, root, prefabPath);
                        break;
                    case "delete":
                        result = ExecuteDelete(parameters, root, prefabPath);
                        break;
                    case "reparent":
                        result = ExecuteReparent(parameters, root, prefabPath);
                        break;
                    default:
                        throw new PluginException("ERR_INVALID_PARAMS",
                            $"action must be create|update|delete|reparent, got: {action}");
                }

                SavePrefab(root, prefabPath);
                return result;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static object ExecuteCreate(JObject parameters, GameObject prefabRoot, string prefabPath)
        {
            var name = Payload.GetString(parameters, "name");
            if (string.IsNullOrEmpty(name))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "name is required for 'create' action");
            }

            var parentPath = Payload.GetString(parameters, "parent_path");
            Transform parentTransform;
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parentGo = PrefabGameObjectResolver.Resolve(prefabRoot, parentPath);
                if (parentGo == null)
                {
                    throw new PluginException(SceneToolErrors.ObjectNotFound,
                        $"Parent GameObject not found in prefab: {parentPath}");
                }

                parentTransform = parentGo.transform;
            }
            else
            {
                parentTransform = prefabRoot.transform;
            }

            var primitiveType = Payload.GetString(parameters, "primitive_type");

            GameObject go;
            if (!string.IsNullOrEmpty(primitiveType))
            {
                var pt = ParsePrimitiveType(primitiveType);
                go = GameObject.CreatePrimitive(pt);
                go.name = name;
                go.transform.SetParent(parentTransform, false);
            }
            else
            {
                go = new GameObject(name);
                go.transform.SetParent(parentTransform, false);
            }

            ApplyOptionalProperties(go, parameters);

            var siblingIndex = Payload.GetInt(parameters, "sibling_index");
            if (siblingIndex.HasValue)
            {
                go.transform.SetSiblingIndex(siblingIndex.Value);
            }

            var result = new JObject
            {
                ["action"] = "create",
                ["prefab_path"] = prefabPath,
                ["game_object_path"] = PrefabGameObjectResolver.GetRelativePath(prefabRoot, go),
                ["game_object_name"] = go.name,
            };

            var parentRelPath = PrefabGameObjectResolver.GetRelativePath(prefabRoot, parentTransform.gameObject);
            result["parent_path"] = parentRelPath;

            if (!string.IsNullOrEmpty(primitiveType))
            {
                result["primitive_type"] = primitiveType;
            }

            return result;
        }

        private static object ExecuteUpdate(JObject parameters, GameObject prefabRoot, string prefabPath)
        {
            var gameObjectPath = Payload.GetString(parameters, "game_object_path");
            if (string.IsNullOrEmpty(gameObjectPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "game_object_path is required for 'update' action");
            }

            var go = PrefabGameObjectResolver.Resolve(prefabRoot, gameObjectPath);
            if (go == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"GameObject not found in prefab: {gameObjectPath}");
            }

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

            var active = ManageGameObjectTool.GetBool(parameters, "active");
            if (active.HasValue)
            {
                go.SetActive(active.Value);
                propertiesSet.Add("active");
            }

            return new JObject
            {
                ["action"] = "update",
                ["prefab_path"] = prefabPath,
                ["game_object_path"] = PrefabGameObjectResolver.GetRelativePath(prefabRoot, go),
                ["game_object_name"] = go.name,
                ["previous_path"] = previousPath,
                ["properties_set"] = new JArray(propertiesSet.ToArray()),
            };
        }

        private static object ExecuteDelete(JObject parameters, GameObject prefabRoot, string prefabPath)
        {
            var gameObjectPath = Payload.GetString(parameters, "game_object_path");
            if (string.IsNullOrEmpty(gameObjectPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "game_object_path is required for 'delete' action");
            }

            var go = PrefabGameObjectResolver.Resolve(prefabRoot, gameObjectPath);
            if (go == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"GameObject not found in prefab: {gameObjectPath}");
            }

            if (go == prefabRoot)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "Cannot delete the prefab root GameObject");
            }

            var childrenDeleted = ManageGameObjectTool.CountDescendants(go.transform);
            var goName = go.name;
            var goPath = PrefabGameObjectResolver.GetRelativePath(prefabRoot, go);

            Object.DestroyImmediate(go);

            return new JObject
            {
                ["action"] = "delete",
                ["prefab_path"] = prefabPath,
                ["game_object_path"] = goPath,
                ["game_object_name"] = goName,
                ["children_deleted"] = childrenDeleted,
            };
        }

        private static object ExecuteReparent(JObject parameters, GameObject prefabRoot, string prefabPath)
        {
            var gameObjectPath = Payload.GetString(parameters, "game_object_path");
            if (string.IsNullOrEmpty(gameObjectPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "game_object_path is required for 'reparent' action");
            }

            var go = PrefabGameObjectResolver.Resolve(prefabRoot, gameObjectPath);
            if (go == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"GameObject not found in prefab: {gameObjectPath}");
            }

            if (go == prefabRoot)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "Cannot reparent the prefab root GameObject");
            }

            var previousParentPath = go.transform.parent != null
                ? PrefabGameObjectResolver.GetRelativePath(prefabRoot, go.transform.parent.gameObject)
                : null;

            var parentPath = Payload.GetString(parameters, "parent_path");
            Transform newParent;
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parentGo = PrefabGameObjectResolver.Resolve(prefabRoot, parentPath);
                if (parentGo == null)
                {
                    throw new PluginException(SceneToolErrors.ObjectNotFound,
                        $"New parent GameObject not found in prefab: {parentPath}");
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
            else
            {
                newParent = prefabRoot.transform;
            }

            var worldPositionStays = ManageGameObjectTool.GetBool(parameters, "world_position_stays") ?? true;

            go.transform.SetParent(newParent, worldPositionStays);

            var siblingIndex = Payload.GetInt(parameters, "sibling_index");
            if (siblingIndex.HasValue)
            {
                go.transform.SetSiblingIndex(siblingIndex.Value);
            }

            var result = new JObject
            {
                ["action"] = "reparent",
                ["prefab_path"] = prefabPath,
                ["game_object_path"] = PrefabGameObjectResolver.GetRelativePath(prefabRoot, go),
                ["game_object_name"] = go.name,
                ["previous_parent_path"] = previousParentPath != null ? (JToken)previousParentPath : JValue.CreateNull(),
                ["new_parent_path"] = PrefabGameObjectResolver.GetRelativePath(prefabRoot, newParent.gameObject),
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

            var active = ManageGameObjectTool.GetBool(parameters, "active");
            if (active.HasValue)
            {
                go.SetActive(active.Value);
            }
        }

        private static void ValidateTag(string tag)
        {
            try
            {
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
    }
}
