using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal static class FieldSerializer
    {
        internal struct SerializeResult
        {
            internal JObject Fields;
            internal bool FieldsTruncated;
        }

        internal static SerializeResult Serialize(
            Component component,
            HashSet<string> fieldFilter,
            int maxArrayElements,
            ToolContext context = null)
        {
            var so = new SerializedObject(component);
            var fields = new JObject();
            var fieldCount = 0;
            var truncated = false;

            var iterator = so.GetIterator();
            if (!iterator.NextVisible(true))
            {
                return new SerializeResult { Fields = fields, FieldsTruncated = false };
            }

            do
            {
                var name = iterator.name;
                if (ExcludedProperties.Contains(name))
                {
                    continue;
                }

                if (fieldFilter != null && !fieldFilter.Contains(name))
                {
                    continue;
                }

                if (fieldCount >= SceneToolLimits.MaxFieldCount)
                {
                    truncated = true;
                    break;
                }

                var value = SerializeProperty(iterator, maxArrayElements, 0, ref fieldCount, context);
                fields[name] = value;
                fieldCount++;

                if (fieldCount >= SceneToolLimits.MaxFieldCount)
                {
                    truncated = true;
                    break;
                }
            }
            while (iterator.NextVisible(false));

            return new SerializeResult { Fields = fields, FieldsTruncated = truncated };
        }

        private static JToken SerializeProperty(SerializedProperty prop, int maxArrayElements, int depth, ref int fieldCount, ToolContext context = null)
        {
            if (depth > SceneToolLimits.MaxNestingDepth)
            {
                return new JValue("...");
            }

            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    if (prop.type == "long")
                    {
                        return new JValue(prop.longValue);
                    }

                    return new JValue(prop.intValue);

                case SerializedPropertyType.Float:
                    if (prop.type == "double")
                    {
                        return new JValue(prop.doubleValue);
                    }

                    return new JValue(prop.floatValue);

                case SerializedPropertyType.Boolean:
                    return new JValue(prop.boolValue);

                case SerializedPropertyType.String:
                    return new JValue(prop.stringValue);

                case SerializedPropertyType.Vector2:
                    return Wrap("UnityEngine.Vector2", new JObject
                    {
                        ["x"] = prop.vector2Value.x,
                        ["y"] = prop.vector2Value.y
                    });

                case SerializedPropertyType.Vector3:
                    return Wrap("UnityEngine.Vector3", new JObject
                    {
                        ["x"] = prop.vector3Value.x,
                        ["y"] = prop.vector3Value.y,
                        ["z"] = prop.vector3Value.z
                    });

                case SerializedPropertyType.Vector4:
                    return Wrap("UnityEngine.Vector4", new JObject
                    {
                        ["x"] = prop.vector4Value.x,
                        ["y"] = prop.vector4Value.y,
                        ["z"] = prop.vector4Value.z,
                        ["w"] = prop.vector4Value.w
                    });

                case SerializedPropertyType.Vector2Int:
                    return Wrap("UnityEngine.Vector2Int", new JObject
                    {
                        ["x"] = prop.vector2IntValue.x,
                        ["y"] = prop.vector2IntValue.y
                    });

                case SerializedPropertyType.Vector3Int:
                    return Wrap("UnityEngine.Vector3Int", new JObject
                    {
                        ["x"] = prop.vector3IntValue.x,
                        ["y"] = prop.vector3IntValue.y,
                        ["z"] = prop.vector3IntValue.z
                    });

                case SerializedPropertyType.Quaternion:
                    return Wrap("UnityEngine.Quaternion", new JObject
                    {
                        ["x"] = prop.quaternionValue.x,
                        ["y"] = prop.quaternionValue.y,
                        ["z"] = prop.quaternionValue.z,
                        ["w"] = prop.quaternionValue.w
                    });

                case SerializedPropertyType.Color:
                    return Wrap("UnityEngine.Color", new JObject
                    {
                        ["r"] = prop.colorValue.r,
                        ["g"] = prop.colorValue.g,
                        ["b"] = prop.colorValue.b,
                        ["a"] = prop.colorValue.a
                    });

                case SerializedPropertyType.Rect:
                    return Wrap("UnityEngine.Rect", new JObject
                    {
                        ["x"] = prop.rectValue.x,
                        ["y"] = prop.rectValue.y,
                        ["width"] = prop.rectValue.width,
                        ["height"] = prop.rectValue.height
                    });

                case SerializedPropertyType.RectInt:
                    return Wrap("UnityEngine.RectInt", new JObject
                    {
                        ["x"] = prop.rectIntValue.x,
                        ["y"] = prop.rectIntValue.y,
                        ["width"] = prop.rectIntValue.width,
                        ["height"] = prop.rectIntValue.height
                    });

                case SerializedPropertyType.Bounds:
                    return Wrap("UnityEngine.Bounds", new JObject
                    {
                        ["center"] = new JObject
                        {
                            ["x"] = prop.boundsValue.center.x,
                            ["y"] = prop.boundsValue.center.y,
                            ["z"] = prop.boundsValue.center.z
                        },
                        ["size"] = new JObject
                        {
                            ["x"] = prop.boundsValue.size.x,
                            ["y"] = prop.boundsValue.size.y,
                            ["z"] = prop.boundsValue.size.z
                        }
                    });

                case SerializedPropertyType.BoundsInt:
                    return Wrap("UnityEngine.BoundsInt", new JObject
                    {
                        ["position"] = new JObject
                        {
                            ["x"] = prop.boundsIntValue.position.x,
                            ["y"] = prop.boundsIntValue.position.y,
                            ["z"] = prop.boundsIntValue.position.z
                        },
                        ["size"] = new JObject
                        {
                            ["x"] = prop.boundsIntValue.size.x,
                            ["y"] = prop.boundsIntValue.size.y,
                            ["z"] = prop.boundsIntValue.size.z
                        }
                    });

                case SerializedPropertyType.LayerMask:
                    return Wrap("UnityEngine.LayerMask", new JValue(prop.intValue));

                case SerializedPropertyType.AnimationCurve:
                    return SerializeAnimationCurve(prop);

                case SerializedPropertyType.Gradient:
                    return SerializeGradient(prop);

                case SerializedPropertyType.Enum:
                    return SerializeEnum(prop);

                case SerializedPropertyType.ObjectReference:
                    return SerializeObjectReference(prop, context);

                case SerializedPropertyType.ArraySize:
                    return new JValue(prop.intValue);

                default:
                    if (prop.isArray)
                    {
                        return SerializeArray(prop, maxArrayElements, depth, ref fieldCount, context);
                    }

                    if (prop.hasChildren)
                    {
                        return SerializeGeneric(prop, maxArrayElements, depth, ref fieldCount, context);
                    }

                    return JValue.CreateNull();
            }
        }

        private static JToken SerializeAnimationCurve(SerializedProperty prop)
        {
            var curve = prop.animationCurveValue;
            float minTime = 0, maxTime = 0;
            if (curve != null && curve.length > 0)
            {
                minTime = curve[0].time;
                maxTime = curve[curve.length - 1].time;
            }

            return new JObject
            {
                ["type"] = "UnityEngine.AnimationCurve",
                ["value"] = new JObject
                {
                    ["keyCount"] = curve?.length ?? 0,
                    ["timeRange"] = new JArray(minTime, maxTime)
                }
            };
        }

        private static JToken SerializeGradient(SerializedProperty prop)
        {
            var gradient = prop.gradientValue;
            return new JObject
            {
                ["type"] = "UnityEngine.Gradient",
                ["value"] = new JObject
                {
                    ["colorKeyCount"] = gradient?.colorKeys?.Length ?? 0,
                    ["alphaKeyCount"] = gradient?.alphaKeys?.Length ?? 0,
                    ["mode"] = gradient?.mode.ToString() ?? "Blend"
                }
            };
        }

        private static JToken SerializeEnum(SerializedProperty prop)
        {
            var enumNames = prop.enumNames;
            var idx = prop.enumValueIndex;
            string valueName;
            if (enumNames != null && idx >= 0 && idx < enumNames.Length)
            {
                valueName = enumNames[idx];
            }
            else
            {
                valueName = idx.ToString();
            }

            return new JObject
            {
                ["type"] = prop.type,
                ["value"] = valueName
            };
        }

        private static JToken SerializeObjectReference(SerializedProperty prop, ToolContext context = null)
        {
            var obj = prop.objectReferenceValue;
            if (obj == null)
            {
                return JValue.CreateNull();
            }

            var typeName = prop.type;
            if (typeName.StartsWith("PPtr<") && typeName.EndsWith(">"))
            {
                typeName = typeName.Substring(5, typeName.Length - 6);
            }

            var objType = obj.GetType();
            var fullTypeName = objType.FullName ?? typeName;

            var wrapper = new JObject
            {
                ["type"] = fullTypeName,
                ["value"] = obj.name + (obj is Component ? $" ({objType.Name})" : "")
            };

            if (EditorUtility.IsPersistent(obj))
            {
                var assetPath = AssetDatabase.GetAssetPath(obj);
                if (context != null && context.IsPrefabContext && context.IsSamePrefabReference(assetPath))
                {
                    wrapper["is_object_ref"] = true;
                    GameObject refGo = null;
                    if (obj is GameObject go)
                    {
                        refGo = go;
                    }
                    else if (obj is Component comp)
                    {
                        refGo = comp.gameObject;
                    }

                    if (refGo != null)
                    {
                        wrapper["ref_path"] = context.GetPath(refGo);
                    }
                }
                else
                {
                    wrapper["is_asset_ref"] = true;
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        wrapper["asset_path"] = assetPath;
                    }
                }
            }
            else
            {
                wrapper["is_object_ref"] = true;
                GameObject refGo = null;
                if (obj is GameObject go)
                {
                    refGo = go;
                }
                else if (obj is Component comp)
                {
                    refGo = comp.gameObject;
                }

                if (refGo != null)
                {
                    var path = context != null ? context.GetPath(refGo) : GameObjectResolver.GetHierarchyPath(refGo);
                    wrapper["ref_path"] = path;
                }
            }

            return wrapper;
        }

        private static JToken SerializeArray(SerializedProperty prop, int maxArrayElements, int depth, ref int fieldCount, ToolContext context = null)
        {
            var totalCount = prop.arraySize;
            var typeName = prop.arrayElementType;

            if (prop.arraySize > 0)
            {
                var elem = prop.GetArrayElementAtIndex(0);
                var elemType = elem.type;
                typeName = elemType + "[]";
            }
            else
            {
                typeName = (typeName ?? "unknown") + "[]";
            }

            var wrapper = new JObject
            {
                ["type"] = typeName
            };

            if (maxArrayElements == 0)
            {
                wrapper["_total_count"] = totalCount;
                return wrapper;
            }

            var count = System.Math.Min(totalCount, maxArrayElements);
            var array = new JArray();
            for (var i = 0; i < count; i++)
            {
                var elem = prop.GetArrayElementAtIndex(i);
                array.Add(SerializeProperty(elem, maxArrayElements, depth + 1, ref fieldCount, context));
            }

            wrapper["value"] = array;

            if (totalCount > maxArrayElements)
            {
                wrapper["_truncated"] = true;
                wrapper["_total_count"] = totalCount;
            }

            return wrapper;
        }

        private static JToken SerializeGeneric(SerializedProperty prop, int maxArrayElements, int depth, ref int fieldCount, ToolContext context = null)
        {
            var typeName = prop.type;
            var value = new JObject();
            var child = prop.Copy();
            var end = child.GetEndProperty();

            if (!child.NextVisible(true))
            {
                return new JObject { ["type"] = typeName, ["value"] = value };
            }

            do
            {
                if (SerializedProperty.EqualContents(child, end))
                {
                    break;
                }

                var name = child.name;
                if (ExcludedProperties.Contains(name))
                {
                    continue;
                }

                if (fieldCount >= SceneToolLimits.MaxFieldCount)
                {
                    break;
                }

                value[name] = SerializeProperty(child, maxArrayElements, depth + 1, ref fieldCount, context);
                fieldCount++;
            }
            while (child.NextVisible(false));

            return new JObject
            {
                ["type"] = typeName,
                ["value"] = value
            };
        }

        private static JObject Wrap(string typeName, JToken value)
        {
            return new JObject
            {
                ["type"] = typeName,
                ["value"] = value
            };
        }
    }
}
