using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal static class FieldDeserializer
    {
        internal struct ApplyResult
        {
            internal List<string> FieldsSet;
            internal List<string> FieldsSkipped;
        }

        internal static ApplyResult Apply(
            SerializedObject so,
            JObject fields,
            Dictionary<string, ResolvedRef> resolvedRefs)
        {
            var fieldsSet = new List<string>();
            var fieldsSkipped = new List<string>();

            foreach (var prop in fields.Properties())
            {
                var name = prop.Name;
                var serializedProp = so.FindProperty(name);
                if (serializedProp == null)
                {
                    fieldsSkipped.Add(name);
                    continue;
                }

                ApplyValue(serializedProp, prop.Value, resolvedRefs, name, 0);
                fieldsSet.Add(name);
            }

            return new ApplyResult { FieldsSet = fieldsSet, FieldsSkipped = fieldsSkipped };
        }

        private static void ApplyValue(
            SerializedProperty prop,
            JToken value,
            Dictionary<string, ResolvedRef> resolvedRefs,
            string refPath,
            int depth)
        {
            if (depth > SceneToolLimits.MaxNestingDepth)
            {
                return;
            }

            if (value == null || value.Type == JTokenType.Null)
            {
                if (prop.propertyType == SerializedPropertyType.ObjectReference)
                {
                    prop.objectReferenceValue = null;
                }

                return;
            }

            if (value is JObject obj && obj.ContainsKey("$ref"))
            {
                if (resolvedRefs.TryGetValue(refPath, out var resolved))
                {
                    prop.objectReferenceValue = resolved.Object;
                }

                return;
            }

            if (value is JObject assetObj && assetObj.ContainsKey("$asset"))
            {
                if (resolvedRefs.TryGetValue(refPath, out var resolved))
                {
                    prop.objectReferenceValue = resolved.Object;
                }

                return;
            }

            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    if (prop.type == "long")
                    {
                        prop.longValue = value.Value<long>();
                    }
                    else
                    {
                        prop.intValue = value.Value<int>();
                    }

                    break;

                case SerializedPropertyType.Float:
                    if (prop.type == "double")
                    {
                        prop.doubleValue = value.Value<double>();
                    }
                    else
                    {
                        prop.floatValue = value.Value<float>();
                    }

                    break;

                case SerializedPropertyType.Boolean:
                    prop.boolValue = value.Value<bool>();
                    break;

                case SerializedPropertyType.String:
                    prop.stringValue = value.Value<string>();
                    break;

                case SerializedPropertyType.Vector2:
                    prop.vector2Value = ParseVector2(value);
                    break;

                case SerializedPropertyType.Vector3:
                    prop.vector3Value = ParseVector3(value);
                    break;

                case SerializedPropertyType.Vector4:
                    prop.vector4Value = ParseVector4(value);
                    break;

                case SerializedPropertyType.Vector2Int:
                    prop.vector2IntValue = ParseVector2Int(value);
                    break;

                case SerializedPropertyType.Vector3Int:
                    prop.vector3IntValue = ParseVector3Int(value);
                    break;

                case SerializedPropertyType.Quaternion:
                    prop.quaternionValue = ParseQuaternion(value);
                    break;

                case SerializedPropertyType.Color:
                    prop.colorValue = ParseColor(value);
                    break;

                case SerializedPropertyType.Rect:
                    prop.rectValue = ParseRect(value);
                    break;

                case SerializedPropertyType.RectInt:
                    prop.rectIntValue = ParseRectInt(value);
                    break;

                case SerializedPropertyType.Bounds:
                    prop.boundsValue = ParseBounds(value);
                    break;

                case SerializedPropertyType.BoundsInt:
                    prop.boundsIntValue = ParseBoundsInt(value);
                    break;

                case SerializedPropertyType.LayerMask:
                    prop.intValue = value.Value<int>();
                    break;

                case SerializedPropertyType.AnimationCurve:
                    prop.animationCurveValue = ParseAnimationCurve(value);
                    break;

                case SerializedPropertyType.Gradient:
                    prop.gradientValue = ParseGradient(value);
                    break;

                case SerializedPropertyType.Enum:
                    ApplyEnum(prop, value);
                    break;

                case SerializedPropertyType.ObjectReference:
                    if (resolvedRefs.TryGetValue(refPath, out var refObj))
                    {
                        prop.objectReferenceValue = refObj.Object;
                    }

                    break;

                default:
                    if (prop.isArray && value is JArray arr)
                    {
                        ApplyArray(prop, arr, resolvedRefs, refPath, depth);
                    }
                    else if (prop.hasChildren && value is JObject childObj)
                    {
                        ApplySerializable(prop, childObj, resolvedRefs, refPath, depth);
                    }

                    break;
            }
        }

        private static void ApplyArray(
            SerializedProperty prop,
            JArray array,
            Dictionary<string, ResolvedRef> resolvedRefs,
            string refPath,
            int depth)
        {
            prop.ClearArray();
            for (var i = 0; i < array.Count; i++)
            {
                prop.InsertArrayElementAtIndex(i);
                var elem = prop.GetArrayElementAtIndex(i);
                var elemPath = refPath + "[" + i + "]";
                ApplyValue(elem, array[i], resolvedRefs, elemPath, depth + 1);
            }
        }

        private static void ApplySerializable(
            SerializedProperty prop,
            JObject obj,
            Dictionary<string, ResolvedRef> resolvedRefs,
            string refPath,
            int depth)
        {
            foreach (var field in obj.Properties())
            {
                var childProp = prop.FindPropertyRelative(field.Name);
                if (childProp == null)
                {
                    continue;
                }

                var childPath = refPath + "." + field.Name;
                ApplyValue(childProp, field.Value, resolvedRefs, childPath, depth + 1);
            }
        }

        private static void ApplyEnum(SerializedProperty prop, JToken value)
        {
            if (value.Type == JTokenType.Integer)
            {
                prop.enumValueIndex = value.Value<int>();
                return;
            }

            if (value.Type == JTokenType.String)
            {
                var names = prop.enumNames;
                var target = value.Value<string>();
                for (var i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], target, StringComparison.OrdinalIgnoreCase))
                    {
                        prop.enumValueIndex = i;
                        return;
                    }
                }

                if (int.TryParse(target, out var idx))
                {
                    prop.enumValueIndex = idx;
                }
            }
        }

        private static Vector2 ParseVector2(JToken value)
        {
            return new Vector2(
                value.Value<float>("x"),
                value.Value<float>("y"));
        }

        private static Vector3 ParseVector3(JToken value)
        {
            return new Vector3(
                value.Value<float>("x"),
                value.Value<float>("y"),
                value.Value<float>("z"));
        }

        private static Vector4 ParseVector4(JToken value)
        {
            return new Vector4(
                value.Value<float>("x"),
                value.Value<float>("y"),
                value.Value<float>("z"),
                value.Value<float>("w"));
        }

        private static Vector2Int ParseVector2Int(JToken value)
        {
            return new Vector2Int(
                value.Value<int>("x"),
                value.Value<int>("y"));
        }

        private static Vector3Int ParseVector3Int(JToken value)
        {
            return new Vector3Int(
                value.Value<int>("x"),
                value.Value<int>("y"),
                value.Value<int>("z"));
        }

        private static Quaternion ParseQuaternion(JToken value)
        {
            return new Quaternion(
                value.Value<float>("x"),
                value.Value<float>("y"),
                value.Value<float>("z"),
                value.Value<float>("w"));
        }

        private static Color ParseColor(JToken value)
        {
            var a = 1.0f;
            if (value["a"] != null)
            {
                a = value.Value<float>("a");
            }

            return new Color(
                value.Value<float>("r"),
                value.Value<float>("g"),
                value.Value<float>("b"),
                a);
        }

        private static Rect ParseRect(JToken value)
        {
            return new Rect(
                value.Value<float>("x"),
                value.Value<float>("y"),
                value.Value<float>("width"),
                value.Value<float>("height"));
        }

        private static RectInt ParseRectInt(JToken value)
        {
            return new RectInt(
                value.Value<int>("x"),
                value.Value<int>("y"),
                value.Value<int>("width"),
                value.Value<int>("height"));
        }

        private static Bounds ParseBounds(JToken value)
        {
            var center = value["center"];
            var size = value["size"];
            return new Bounds(
                new Vector3(center.Value<float>("x"), center.Value<float>("y"), center.Value<float>("z")),
                new Vector3(size.Value<float>("x"), size.Value<float>("y"), size.Value<float>("z")));
        }

        private static BoundsInt ParseBoundsInt(JToken value)
        {
            var pos = value["position"];
            var size = value["size"];
            return new BoundsInt(
                new Vector3Int(pos.Value<int>("x"), pos.Value<int>("y"), pos.Value<int>("z")),
                new Vector3Int(size.Value<int>("x"), size.Value<int>("y"), size.Value<int>("z")));
        }

        private static AnimationCurve ParseAnimationCurve(JToken value)
        {
            var curve = new AnimationCurve();
            if (value is JObject obj)
            {
                if (obj["keys"] is JArray keys)
                {
                    foreach (var key in keys)
                    {
                        var kf = new Keyframe(
                            key.Value<float>("time"),
                            key.Value<float>("value"));
                        if (key["inTangent"] != null)
                        {
                            kf.inTangent = key.Value<float>("inTangent");
                        }

                        if (key["outTangent"] != null)
                        {
                            kf.outTangent = key.Value<float>("outTangent");
                        }

                        if (key["inWeight"] != null)
                        {
                            kf.inWeight = key.Value<float>("inWeight");
                        }

                        if (key["outWeight"] != null)
                        {
                            kf.outWeight = key.Value<float>("outWeight");
                        }

                        if (key["weightedMode"] != null)
                        {
                            if (Enum.TryParse<WeightedMode>(key.Value<string>("weightedMode"), true, out var wm))
                            {
                                kf.weightedMode = wm;
                            }
                        }

                        curve.AddKey(kf);
                    }
                }

                if (obj["preWrapMode"] != null)
                {
                    if (Enum.TryParse<WrapMode>(obj.Value<string>("preWrapMode"), true, out var pre))
                    {
                        curve.preWrapMode = pre;
                    }
                }

                if (obj["postWrapMode"] != null)
                {
                    if (Enum.TryParse<WrapMode>(obj.Value<string>("postWrapMode"), true, out var post))
                    {
                        curve.postWrapMode = post;
                    }
                }
            }

            return curve;
        }

        private static Gradient ParseGradient(JToken value)
        {
            var gradient = new Gradient();
            if (value is JObject obj)
            {
                if (obj["colorKeys"] is JArray colorKeysArr)
                {
                    var colorKeys = new GradientColorKey[colorKeysArr.Count];
                    for (var i = 0; i < colorKeysArr.Count; i++)
                    {
                        var ck = colorKeysArr[i];
                        var color = ParseColor(ck["color"]);
                        colorKeys[i] = new GradientColorKey(color, ck.Value<float>("time"));
                    }

                    var alphaKeys = gradient.alphaKeys;
                    if (obj["alphaKeys"] is JArray alphaKeysArr)
                    {
                        alphaKeys = new GradientAlphaKey[alphaKeysArr.Count];
                        for (var i = 0; i < alphaKeysArr.Count; i++)
                        {
                            var ak = alphaKeysArr[i];
                            alphaKeys[i] = new GradientAlphaKey(ak.Value<float>("alpha"), ak.Value<float>("time"));
                        }
                    }

                    gradient.SetKeys(colorKeys, alphaKeys);
                }
                else if (obj["alphaKeys"] is JArray alphaKeysOnly)
                {
                    var alphaKeys = new GradientAlphaKey[alphaKeysOnly.Count];
                    for (var i = 0; i < alphaKeysOnly.Count; i++)
                    {
                        var ak = alphaKeysOnly[i];
                        alphaKeys[i] = new GradientAlphaKey(ak.Value<float>("alpha"), ak.Value<float>("time"));
                    }

                    gradient.SetKeys(gradient.colorKeys, alphaKeys);
                }

                if (obj["mode"] != null)
                {
                    if (Enum.TryParse<GradientMode>(obj.Value<string>("mode"), true, out var mode))
                    {
                        gradient.mode = mode;
                    }
                }
            }

            return gradient;
        }
    }
}
