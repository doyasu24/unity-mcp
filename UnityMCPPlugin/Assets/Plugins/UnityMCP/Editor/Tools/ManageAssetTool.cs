using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal sealed class ManageAssetTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.ManageAsset;

        public override object Execute(JObject parameters)
        {
            if (EditorApplication.isPlaying)
            {
                throw new PluginException(SceneToolErrors.PlayModeActive,
                    "Cannot manage assets while in Play Mode. Use control_play_mode to stop playback first.");
            }

            var action = Payload.GetString(parameters, "action");
            if (!ManageAssetActions.IsSupported(action))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"action must be {ManageAssetActions.Create}|{ManageAssetActions.Delete}|{ManageAssetActions.GetProperties}|{ManageAssetActions.SetProperties}|{ManageAssetActions.SetShader}|{ManageAssetActions.GetKeywords}|{ManageAssetActions.SetKeywords}");
            }

            var assetPath = Payload.GetString(parameters, "asset_path");
            if (string.IsNullOrEmpty(assetPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "asset_path is required");
            }

            switch (action)
            {
                case ManageAssetActions.Create:
                    return ExecuteCreate(parameters, assetPath);
                case ManageAssetActions.Delete:
                    return ExecuteDelete(assetPath);
                default:
                    return ManageMaterialTool.Execute(parameters);
            }
        }

        private static ManageAssetPayload ExecuteCreate(JObject parameters, string assetPath)
        {
            var assetType = Payload.GetString(parameters, "asset_type");
            if (string.IsNullOrEmpty(assetType) || !AssetTypes.IsSupported(assetType))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"asset_type is required for 'create' and must be one of: {AssetTypes.Material}|{AssetTypes.Folder}|{AssetTypes.PhysicMaterial}|{AssetTypes.AnimatorController}|{AssetTypes.RenderTexture}");
            }

            var overwrite = Payload.GetBool(parameters, "overwrite") ?? false;

            if (assetType != AssetTypes.Folder)
            {
                var parentDir = System.IO.Path.GetDirectoryName(assetPath);
                if (!string.IsNullOrEmpty(parentDir) && !AssetDatabase.IsValidFolder(parentDir))
                {
                    throw new PluginException("ERR_INVALID_PARAMS",
                        $"Parent directory does not exist: {parentDir}");
                }
            }

            if (!overwrite && AssetExists(assetPath, assetType))
            {
                throw new PluginException("ERR_ASSET_EXISTS",
                    $"Asset already exists at path: {assetPath}. Set overwrite=true to replace it.");
            }

            switch (assetType)
            {
                case AssetTypes.Material:
                    CreateMaterial(parameters, assetPath);
                    break;
                case AssetTypes.Folder:
                    CreateFolder(assetPath);
                    break;
                case AssetTypes.PhysicMaterial:
                    CreatePhysicMaterial(assetPath);
                    break;
                case AssetTypes.AnimatorController:
                    CreateAnimatorController(assetPath);
                    break;
                case AssetTypes.RenderTexture:
                    CreateRenderTexture(parameters, assetPath);
                    break;
            }

            AssetDatabase.SaveAssets();
            return new ManageAssetPayload(ManageAssetActions.Create, assetPath, assetType, true);
        }

        private static ManageAssetPayload ExecuteDelete(string assetPath)
        {
            var assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(assetGuid))
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"Asset not found at path: {assetPath}");
            }

            var deleted = AssetDatabase.DeleteAsset(assetPath);
            if (!deleted)
            {
                throw new PluginException("ERR_UNITY_EXECUTION",
                    $"Failed to delete asset at path: {assetPath}");
            }

            var assetType = AssetDatabase.IsValidFolder(assetPath) ? AssetTypes.Folder : "unknown";
            return new ManageAssetPayload(ManageAssetActions.Delete, assetPath, assetType, true);
        }

        private static bool AssetExists(string assetPath, string assetType)
        {
            if (assetType == AssetTypes.Folder)
            {
                return AssetDatabase.IsValidFolder(assetPath);
            }

            return !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets));
        }

        private static void CreateMaterial(JObject parameters, string assetPath)
        {
            var shaderName = "Standard";
            var props = parameters["properties"] as JObject;
            if (props != null)
            {
                var sn = props["shader_name"]?.Value<string>();
                if (!string.IsNullOrEmpty(sn))
                {
                    shaderName = sn;
                }
            }

            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"Shader not found: {shaderName}");
            }

            var mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, assetPath);
        }

        private static void CreateFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                return;
            }

            var parent = System.IO.Path.GetDirectoryName(assetPath);
            var folderName = System.IO.Path.GetFileName(assetPath);
            if (string.IsNullOrEmpty(parent))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    "Cannot create a folder at the root level. Use a path like 'Assets/FolderName'.");
            }

            if (!AssetDatabase.IsValidFolder(parent))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"Parent directory does not exist: {parent}");
            }

            AssetDatabase.CreateFolder(parent, folderName);
        }

        private static void CreatePhysicMaterial(string assetPath)
        {
            var pm = new PhysicsMaterial();
            AssetDatabase.CreateAsset(pm, assetPath);
        }

        private static void CreateAnimatorController(string assetPath)
        {
            AnimatorController.CreateAnimatorControllerAtPath(assetPath);
        }

        private static void CreateRenderTexture(JObject parameters, string assetPath)
        {
            var width = 256;
            var height = 256;
            var depth = 24;
            var props = parameters["properties"] as JObject;
            if (props != null)
            {
                var w = props["width"]?.Value<int>();
                if (w.HasValue) width = w.Value;
                var h = props["height"]?.Value<int>();
                if (h.HasValue) height = h.Value;
                var d = props["depth"]?.Value<int>();
                if (d.HasValue) depth = d.Value;
            }

            var rt = new RenderTexture(width, height, depth);
            AssetDatabase.CreateAsset(rt, assetPath);
        }

    }
}
