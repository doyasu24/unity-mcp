using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal sealed class ResolvedRef
    {
        internal UnityEngine.Object Object;
    }

    internal static class ReferenceResolver
    {
        internal static Dictionary<string, ResolvedRef> ResolveAll(JObject fields, SerializedObject serializedObject)
        {
            var resolved = new Dictionary<string, ResolvedRef>();
            if (fields == null)
            {
                return resolved;
            }

            WalkAndResolve(fields, serializedObject, "", resolved);
            return resolved;
        }

        private static void WalkAndResolve(
            JToken token,
            SerializedObject serializedObject,
            string pathPrefix,
            Dictionary<string, ResolvedRef> resolved)
        {
            if (token is JObject obj)
            {
                if (obj.ContainsKey("$ref"))
                {
                    var refPath = obj.Value<string>("$ref");
                    var componentHint = obj.Value<string>("component");
                    var resolvedObj = ResolveSceneRef(refPath, componentHint, serializedObject, pathPrefix);
                    resolved[pathPrefix] = new ResolvedRef { Object = resolvedObj };
                    return;
                }

                if (obj.ContainsKey("$asset"))
                {
                    var assetPath = obj.Value<string>("$asset");
                    var resolvedObj = ResolveAssetRef(assetPath, serializedObject, pathPrefix);
                    resolved[pathPrefix] = new ResolvedRef { Object = resolvedObj };
                    return;
                }

                foreach (var prop in obj.Properties())
                {
                    var childPath = string.IsNullOrEmpty(pathPrefix)
                        ? prop.Name
                        : pathPrefix + "." + prop.Name;
                    WalkAndResolve(prop.Value, serializedObject, childPath, resolved);
                }
            }
            else if (token is JArray arr)
            {
                for (var i = 0; i < arr.Count; i++)
                {
                    var childPath = pathPrefix + "[" + i + "]";
                    WalkAndResolve(arr[i], serializedObject, childPath, resolved);
                }
            }
        }

        private static UnityEngine.Object ResolveSceneRef(
            string refPath,
            string componentHint,
            SerializedObject serializedObject,
            string fieldPath)
        {
            if (string.IsNullOrEmpty(refPath))
            {
                throw new PluginException(SceneToolErrors.ReferenceNotFound,
                    $"$ref path is empty for field '{fieldPath}'");
            }

            var go = GameObjectResolver.Resolve(refPath);
            if (go == null)
            {
                throw new PluginException(SceneToolErrors.ReferenceNotFound,
                    $"$ref target not found: {refPath}");
            }

            if (!string.IsNullOrEmpty(componentHint))
            {
                var compType = ComponentTypeResolver.Resolve(componentHint);
                if (compType == null)
                {
                    throw new PluginException(SceneToolErrors.ReferenceNotFound,
                        $"Component type '{componentHint}' not found on $ref target '{refPath}'");
                }

                var comp = go.GetComponent(compType);
                if (comp == null)
                {
                    throw new PluginException(SceneToolErrors.ReferenceNotFound,
                        $"Component '{componentHint}' not found on '{refPath}'");
                }

                return comp;
            }

            var fieldProp = FindPropertyForPath(serializedObject, fieldPath);
            if (fieldProp != null)
            {
                var fieldTypeName = fieldProp.type;
                if (fieldTypeName.StartsWith("PPtr<") && fieldTypeName.EndsWith(">"))
                {
                    fieldTypeName = fieldTypeName.Substring(5, fieldTypeName.Length - 6);
                }

                if (fieldTypeName == "GameObject" || fieldTypeName == "$GameObject")
                {
                    return go;
                }

                var fieldType = ComponentTypeResolver.Resolve(fieldTypeName);
                if (fieldType != null && typeof(Component).IsAssignableFrom(fieldType))
                {
                    var comp = go.GetComponent(fieldType);
                    if (comp == null)
                    {
                        throw new PluginException(SceneToolErrors.ReferenceNotFound,
                            $"Component of type '{fieldTypeName}' not found on '{refPath}'");
                    }

                    return comp;
                }
            }

            return go;
        }

        private static UnityEngine.Object ResolveAssetRef(
            string assetPath,
            SerializedObject serializedObject,
            string fieldPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                throw new PluginException(SceneToolErrors.ReferenceNotFound,
                    $"$asset path is empty for field '{fieldPath}'");
            }

            var fieldProp = FindPropertyForPath(serializedObject, fieldPath);
            Type assetType = typeof(UnityEngine.Object);
            if (fieldProp != null)
            {
                var fieldTypeName = fieldProp.type;
                if (fieldTypeName.StartsWith("PPtr<") && fieldTypeName.EndsWith(">"))
                {
                    fieldTypeName = fieldTypeName.Substring(5, fieldTypeName.Length - 6);
                }

                var resolved = ComponentTypeResolver.Resolve(fieldTypeName);
                if (resolved != null)
                {
                    assetType = resolved;
                }
            }

            var obj = AssetDatabase.LoadAssetAtPath(assetPath, assetType);
            if (obj == null)
            {
                throw new PluginException(SceneToolErrors.ReferenceNotFound,
                    $"Asset not found at path: {assetPath}");
            }

            return obj;
        }

        private static SerializedProperty FindPropertyForPath(SerializedObject so, string fieldPath)
        {
            if (string.IsNullOrEmpty(fieldPath))
            {
                return null;
            }

            var cleaned = fieldPath;
            var bracketIdx = cleaned.IndexOf('[');
            if (bracketIdx >= 0)
            {
                cleaned = cleaned.Substring(0, bracketIdx);
            }

            var dotParts = cleaned.Split('.');
            if (dotParts.Length == 0)
            {
                return null;
            }

            var prop = so.FindProperty(dotParts[0]);
            for (var i = 1; i < dotParts.Length && prop != null; i++)
            {
                prop = prop.FindPropertyRelative(dotParts[i]);
            }

            return prop;
        }
    }
}
