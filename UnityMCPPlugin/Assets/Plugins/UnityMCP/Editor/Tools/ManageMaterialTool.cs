using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityMcpPlugin.Tools
{
    internal static class ManageMaterialTool
    {
        internal static object Execute(JObject parameters)
        {
            var action = Payload.GetString(parameters, "action");
            if (!ManageAssetActions.IsSupported(action))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"action must be {ManageAssetActions.GetProperties}|{ManageAssetActions.SetProperties}|{ManageAssetActions.SetShader}|{ManageAssetActions.GetKeywords}|{ManageAssetActions.SetKeywords}");
            }

            var assetPath = Payload.GetString(parameters, "asset_path");
            if (string.IsNullOrEmpty(assetPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "asset_path is required");
            }

            var material = LoadMaterial(assetPath);

            switch (action)
            {
                case ManageAssetActions.GetProperties:
                    return ExecuteGetProperties(material, assetPath);
                case ManageAssetActions.SetProperties:
                    return ExecuteSetProperties(material, assetPath, parameters);
                case ManageAssetActions.SetShader:
                    return ExecuteSetShader(material, assetPath, parameters);
                case ManageAssetActions.GetKeywords:
                    return ExecuteGetKeywords(material, assetPath);
                case ManageAssetActions.SetKeywords:
                    return ExecuteSetKeywords(material, assetPath, parameters);
                default:
                    throw new PluginException("ERR_INVALID_PARAMS", $"unsupported action: {action}");
            }
        }

        private static Material LoadMaterial(string assetPath)
        {
            var asset = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
            if (asset == null)
            {
                throw new PluginException("ERR_ASSET_NOT_FOUND", $"Asset not found at path: {assetPath}");
            }

            var material = asset as Material;
            if (material == null)
            {
                throw new PluginException("ERR_NOT_A_MATERIAL", $"Asset at path is not a Material: {assetPath}");
            }

            return material;
        }

        private static GetMaterialPropertiesPayload ExecuteGetProperties(Material material, string assetPath)
        {
            var shader = material.shader;
            var propertyCount = shader.GetPropertyCount();
            var properties = new List<MaterialPropertyInfo>(propertyCount);

            for (var i = 0; i < propertyCount; i++)
            {
                var propName = shader.GetPropertyName(i);
                var propType = shader.GetPropertyType(i);

                object value;
                string typeName;
                float? rangeMin = null;
                float? rangeMax = null;

                switch (propType)
                {
                    case ShaderPropertyType.Color:
                        var color = material.GetColor(propName);
                        value = new JObject
                        {
                            ["r"] = color.r,
                            ["g"] = color.g,
                            ["b"] = color.b,
                            ["a"] = color.a,
                        };
                        typeName = "Color";
                        break;

                    case ShaderPropertyType.Vector:
                        var vec = material.GetVector(propName);
                        value = new JObject
                        {
                            ["x"] = vec.x,
                            ["y"] = vec.y,
                            ["z"] = vec.z,
                            ["w"] = vec.w,
                        };
                        typeName = "Vector";
                        break;

                    case ShaderPropertyType.Float:
                        value = material.GetFloat(propName);
                        typeName = "Float";
                        break;

                    case ShaderPropertyType.Range:
                        value = material.GetFloat(propName);
                        typeName = "Range";
                        var rangeLimits = shader.GetPropertyRangeLimits(i);
                        rangeMin = rangeLimits.x;
                        rangeMax = rangeLimits.y;
                        break;

                    case ShaderPropertyType.Texture:
                        var tex = material.GetTexture(propName);
                        if (tex != null)
                        {
                            var texPath = AssetDatabase.GetAssetPath(tex);
                            value = new JObject
                            {
                                ["$asset"] = string.IsNullOrEmpty(texPath) ? null : texPath,
                                ["type"] = tex.GetType().Name,
                            };
                        }
                        else
                        {
                            value = null;
                        }
                        typeName = "Texture";
                        break;

                    case ShaderPropertyType.Int:
                        value = material.GetInteger(propName);
                        typeName = "Int";
                        break;

                    default:
                        continue;
                }

                properties.Add(new MaterialPropertyInfo(propName, typeName, value, rangeMin, rangeMax));
            }

            var keywordCount = material.shaderKeywords.Length;
            return new GetMaterialPropertiesPayload(assetPath, shader.name, material.renderQueue, properties, keywordCount);
        }

        private static SetMaterialPropertiesPayload ExecuteSetProperties(Material material, string assetPath, JObject parameters)
        {
            var propsObj = parameters["properties"] as JObject;
            if (propsObj == null || propsObj.Count == 0)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "properties is required for 'set_properties' action");
            }

            var shader = material.shader;
            var propertiesSet = new List<string>();
            var propertiesSkipped = new List<string>();

            foreach (var prop in propsObj)
            {
                var propName = prop.Key;
                if (!material.HasProperty(propName))
                {
                    propertiesSkipped.Add(propName);
                    continue;
                }

                var propIndex = shader.FindPropertyIndex(propName);
                if (propIndex < 0)
                {
                    propertiesSkipped.Add(propName);
                    continue;
                }

                var propType = shader.GetPropertyType(propIndex);
                var propValue = prop.Value;

                switch (propType)
                {
                    case ShaderPropertyType.Color:
                        if (propValue is JObject colorObj)
                        {
                            var color = new Color(
                                colorObj["r"]?.Value<float>() ?? 0f,
                                colorObj["g"]?.Value<float>() ?? 0f,
                                colorObj["b"]?.Value<float>() ?? 0f,
                                colorObj["a"]?.Value<float>() ?? 1f);
                            material.SetColor(propName, color);
                            propertiesSet.Add(propName);
                        }
                        else
                        {
                            propertiesSkipped.Add(propName);
                        }
                        break;

                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        if (propValue != null && propValue.Type != JTokenType.Null)
                        {
                            material.SetFloat(propName, propValue.Value<float>());
                            propertiesSet.Add(propName);
                        }
                        else
                        {
                            propertiesSkipped.Add(propName);
                        }
                        break;

                    case ShaderPropertyType.Int:
                        if (propValue != null && propValue.Type != JTokenType.Null)
                        {
                            material.SetInteger(propName, propValue.Value<int>());
                            propertiesSet.Add(propName);
                        }
                        else
                        {
                            propertiesSkipped.Add(propName);
                        }
                        break;

                    case ShaderPropertyType.Vector:
                        if (propValue is JObject vecObj)
                        {
                            var vec = new Vector4(
                                vecObj["x"]?.Value<float>() ?? 0f,
                                vecObj["y"]?.Value<float>() ?? 0f,
                                vecObj["z"]?.Value<float>() ?? 0f,
                                vecObj["w"]?.Value<float>() ?? 0f);
                            material.SetVector(propName, vec);
                            propertiesSet.Add(propName);
                        }
                        else
                        {
                            propertiesSkipped.Add(propName);
                        }
                        break;

                    case ShaderPropertyType.Texture:
                        if (propValue == null || propValue.Type == JTokenType.Null)
                        {
                            material.SetTexture(propName, null);
                            propertiesSet.Add(propName);
                        }
                        else if (propValue is JObject texObj)
                        {
                            var texAssetPath = texObj["$asset"]?.Value<string>();
                            if (string.IsNullOrEmpty(texAssetPath))
                            {
                                material.SetTexture(propName, null);
                            }
                            else
                            {
                                var tex = AssetDatabase.LoadAssetAtPath<Texture>(texAssetPath);
                                if (tex == null)
                                {
                                    propertiesSkipped.Add(propName);
                                    break;
                                }
                                material.SetTexture(propName, tex);
                            }
                            propertiesSet.Add(propName);
                        }
                        else
                        {
                            propertiesSkipped.Add(propName);
                        }
                        break;

                    default:
                        propertiesSkipped.Add(propName);
                        break;
                }
            }

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return new SetMaterialPropertiesPayload(assetPath, propertiesSet, propertiesSkipped);
        }

        private static SetMaterialShaderPayload ExecuteSetShader(Material material, string assetPath, JObject parameters)
        {
            var shaderName = Payload.GetString(parameters, "shader_name");
            if (string.IsNullOrEmpty(shaderName))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "shader_name is required for 'set_shader' action");
            }

            var previousShader = material.shader.name;
            var newShader = Shader.Find(shaderName);
            if (newShader == null)
            {
                throw new PluginException("ERR_SHADER_NOT_FOUND", $"Shader not found: {shaderName}");
            }

            material.shader = newShader;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return new SetMaterialShaderPayload(assetPath, previousShader, newShader.name);
        }

        private static GetMaterialKeywordsPayload ExecuteGetKeywords(Material material, string assetPath)
        {
            var keywords = new List<string>(material.shaderKeywords);
            return new GetMaterialKeywordsPayload(assetPath, keywords);
        }

        private static SetMaterialKeywordsPayload ExecuteSetKeywords(Material material, string assetPath, JObject parameters)
        {
            var keywordsArray = parameters["keywords"] as JArray;
            if (keywordsArray == null || keywordsArray.Count == 0)
            {
                throw new PluginException("ERR_INVALID_PARAMS", "keywords is required for 'set_keywords' action");
            }

            var keywordsAction = Payload.GetString(parameters, "keywords_action");
            if (!KeywordsActions.IsSupported(keywordsAction))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"keywords_action must be {KeywordsActions.Enable}|{KeywordsActions.Disable}");
            }

            var changed = new List<string>();
            foreach (var token in keywordsArray)
            {
                var keyword = token?.Value<string>();
                if (string.IsNullOrEmpty(keyword))
                {
                    continue;
                }

                if (keywordsAction == KeywordsActions.Enable)
                {
                    if (!material.IsKeywordEnabled(keyword))
                    {
                        material.EnableKeyword(keyword);
                        changed.Add(keyword);
                    }
                }
                else
                {
                    if (material.IsKeywordEnabled(keyword))
                    {
                        material.DisableKeyword(keyword);
                        changed.Add(keyword);
                    }
                }
            }

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return new SetMaterialKeywordsPayload(assetPath, keywordsAction, changed);
        }
    }
}
