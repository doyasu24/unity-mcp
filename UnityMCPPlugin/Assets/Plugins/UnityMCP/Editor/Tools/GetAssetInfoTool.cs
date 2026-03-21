using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal sealed class GetAssetInfoTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.GetAssetInfo;

        public override object Execute(JObject parameters)
        {
            var assetPath = Payload.GetString(parameters, "asset_path");
            if (string.IsNullOrEmpty(assetPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "asset_path is required");
            }

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"Asset not found at path: {assetPath}");
            }

            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            var typeName = asset.GetType().Name;
            var assetName = asset.name;

            long fileSize = 0;
            var fullPath = System.IO.Path.GetFullPath(assetPath);
            if (System.IO.File.Exists(fullPath))
            {
                fileSize = new System.IO.FileInfo(fullPath).Length;
            }

            var properties = BuildProperties(asset);

            return new GetAssetInfoPayload(assetPath, assetName, typeName, guid, fileSize, properties);
        }

        private static JObject BuildProperties(Object asset)
        {
            if (asset is Material mat)
            {
                return BuildMaterialProperties(mat);
            }

            if (asset is Texture2D tex)
            {
                return BuildTexture2DProperties(tex);
            }

            if (asset is AudioClip clip)
            {
                return BuildAudioClipProperties(clip);
            }

            if (asset is Mesh mesh)
            {
                return BuildMeshProperties(mesh);
            }

            if (asset is AnimationClip animClip)
            {
                return BuildAnimationClipProperties(animClip);
            }

            if (asset is ScriptableObject so)
            {
                return BuildScriptableObjectProperties(so);
            }

            return new JObject();
        }

        private static JObject BuildMaterialProperties(Material mat)
        {
            return new JObject
            {
                ["shader"] = mat.shader != null ? mat.shader.name : "None",
                ["render_queue"] = mat.renderQueue,
            };
        }

        private static JObject BuildTexture2DProperties(Texture2D tex)
        {
            return new JObject
            {
                ["width"] = tex.width,
                ["height"] = tex.height,
                ["format"] = tex.format.ToString(),
                ["mip_count"] = tex.mipmapCount,
            };
        }

        private static JObject BuildAudioClipProperties(AudioClip clip)
        {
            return new JObject
            {
                ["length_seconds"] = clip.length,
                ["channels"] = clip.channels,
                ["frequency"] = clip.frequency,
            };
        }

        private static JObject BuildMeshProperties(Mesh mesh)
        {
            var bounds = mesh.bounds;
            return new JObject
            {
                ["vertex_count"] = mesh.vertexCount,
                ["sub_mesh_count"] = mesh.subMeshCount,
                ["bounds"] = new JObject
                {
                    ["center"] = new JObject
                    {
                        ["x"] = bounds.center.x,
                        ["y"] = bounds.center.y,
                        ["z"] = bounds.center.z,
                    },
                    ["size"] = new JObject
                    {
                        ["x"] = bounds.size.x,
                        ["y"] = bounds.size.y,
                        ["z"] = bounds.size.z,
                    },
                },
            };
        }

        private static JObject BuildAnimationClipProperties(AnimationClip clip)
        {
            return new JObject
            {
                ["length_seconds"] = clip.length,
                ["frame_rate"] = clip.frameRate,
            };
        }

        private static JObject BuildScriptableObjectProperties(ScriptableObject so)
        {
            var serializedObject = new SerializedObject(so);
            var fields = new JObject();
            var iterator = serializedObject.GetIterator();
            if (!iterator.NextVisible(true))
            {
                return fields;
            }

            var fieldCount = 0;
            do
            {
                var name = iterator.name;
                if (ExcludedProperties.Contains(name))
                {
                    continue;
                }

                if (fieldCount >= SceneToolLimits.MaxFieldCount)
                {
                    break;
                }

                fields[name] = SerializeSimpleProperty(iterator);
                fieldCount++;
            }
            while (iterator.NextVisible(false));

            return fields;
        }

        private static JToken SerializeSimpleProperty(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.type == "long" ? new JValue(prop.longValue) : new JValue(prop.intValue);
                case SerializedPropertyType.Float:
                    return prop.type == "double" ? new JValue(prop.doubleValue) : new JValue(prop.floatValue);
                case SerializedPropertyType.Boolean:
                    return new JValue(prop.boolValue);
                case SerializedPropertyType.String:
                    return new JValue(prop.stringValue);
                case SerializedPropertyType.Enum:
                    var enumNames = prop.enumNames;
                    var idx = prop.enumValueIndex;
                    if (enumNames != null && idx >= 0 && idx < enumNames.Length)
                    {
                        return new JValue(enumNames[idx]);
                    }

                    return new JValue(idx);
                case SerializedPropertyType.ObjectReference:
                    var obj = prop.objectReferenceValue;
                    return obj != null ? new JValue(obj.name) : JValue.CreateNull();
                default:
                    if (prop.isArray)
                    {
                        return new JObject
                        {
                            ["type"] = (prop.arrayElementType ?? "unknown") + "[]",
                            ["count"] = prop.arraySize,
                        };
                    }

                    return new JValue(prop.type);
            }
        }
    }
}
