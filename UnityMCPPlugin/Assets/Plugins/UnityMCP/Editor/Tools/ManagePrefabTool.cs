using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// シーン上の GameObject と Prefab アセットの関連を管理するツール。
    /// save/apply/unpack は変更操作、get_status は読み取り専用。
    /// prefab 内部の編集（コンポーネント操作など）は既存の unified tools + prefab_path で行う。
    /// </summary>
    internal sealed class ManagePrefabTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.ManagePrefab;

        public override object Execute(JObject parameters)
        {
            var action = Payload.GetString(parameters, "action");
            if (!ManagePrefabActions.IsSupported(action))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"action must be {ManagePrefabActions.Save}|{ManagePrefabActions.Apply}|{ManagePrefabActions.Unpack}|{ManagePrefabActions.GetStatus}");
            }

            // get_status は読み取り専用のため Play Mode チェック不要
            if (action != ManagePrefabActions.GetStatus && EditorApplication.isPlaying)
            {
                throw new PluginException(SceneToolErrors.PlayModeActive,
                    "Cannot modify prefab relationships while in Play Mode. Use control_play_mode to stop playback first.");
            }

            object result;
            switch (action)
            {
                case ManagePrefabActions.Save:
                    result = ExecuteSave(parameters);
                    break;
                case ManagePrefabActions.Apply:
                    result = ExecuteApply(parameters);
                    break;
                case ManagePrefabActions.Unpack:
                    result = ExecuteUnpack(parameters);
                    break;
                case ManagePrefabActions.GetStatus:
                    result = ExecuteGetStatus(parameters);
                    break;
                default:
                    throw new PluginException("ERR_INVALID_PARAMS",
                        $"Unknown action: {action}");
            }

            // get_status は読み取り専用のためシーン保存不要
            if (action != ManagePrefabActions.GetStatus)
            {
                EditorSceneManager.SaveOpenScenes();
            }

            return result;
        }

        private static object ExecuteSave(JObject parameters)
        {
            var prefabPath = Payload.GetString(parameters, "prefab_path");
            if (string.IsNullOrEmpty(prefabPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    "prefab_path is required for 'save' action");
            }

            if (!prefabPath.EndsWith(".prefab"))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    "prefab_path must end with '.prefab'");
            }

            // 親ディレクトリが存在しない場合は再帰的に作成
            var parentDir = System.IO.Path.GetDirectoryName(prefabPath);
            if (!string.IsNullOrEmpty(parentDir) && !AssetDatabase.IsValidFolder(parentDir))
            {
                EnsureDirectoryExists(parentDir);
            }

            var gameObjectPath = Payload.GetString(parameters, "game_object_path");

            // game_object_path 未指定の場合は空の Prefab を作成
            if (string.IsNullOrEmpty(gameObjectPath))
            {
                PrefabHelper.CreateEmptyPrefab(prefabPath);
                return new JObject
                {
                    ["action"] = "save",
                    ["prefab_path"] = prefabPath,
                    ["empty"] = true,
                };
            }

            var go = ResolveSceneGameObject(gameObjectPath);
            var connect = Payload.GetBool(parameters, "connect") ?? false;

            var undoGroup = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("manage_prefab: save");

            bool success;
            GameObject savedPrefab;
            if (connect)
            {
                // シーンのインスタンスと新 Prefab アセットをリンクする
                savedPrefab = PrefabUtility.SaveAsPrefabAssetAndConnect(
                    go, prefabPath, InteractionMode.AutomatedAction, out success);
            }
            else
            {
                savedPrefab = PrefabUtility.SaveAsPrefabAsset(
                    go, prefabPath, out success);
            }

            if (!success || savedPrefab == null)
            {
                throw new PluginException(PrefabToolErrors.PrefabSaveFailed,
                    $"Failed to save prefab at: {prefabPath}");
            }

            Undo.CollapseUndoOperations(undoGroup);

            return new JObject
            {
                ["action"] = "save",
                ["game_object_path"] = gameObjectPath,
                ["prefab_path"] = prefabPath,
                ["connected"] = connect,
            };
        }

        private static object ExecuteApply(JObject parameters)
        {
            var gameObjectPath = RequireGameObjectPath(parameters);
            var go = ResolveSceneGameObject(gameObjectPath);
            EnsurePrefabInstance(go, gameObjectPath);

            // apply 前にアセットパスを取得
            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);

            var undoGroup = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("manage_prefab: apply");

            PrefabUtility.ApplyPrefabInstance(go, InteractionMode.AutomatedAction);

            Undo.CollapseUndoOperations(undoGroup);

            return new JObject
            {
                ["action"] = "apply",
                ["game_object_path"] = gameObjectPath,
                ["prefab_asset_path"] = assetPath,
            };
        }

        private static object ExecuteUnpack(JObject parameters)
        {
            var gameObjectPath = RequireGameObjectPath(parameters);
            var go = ResolveSceneGameObject(gameObjectPath);
            EnsurePrefabInstance(go, gameObjectPath);

            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            var completely = Payload.GetBool(parameters, "completely") ?? false;
            var mode = completely ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;

            var undoGroup = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("manage_prefab: unpack");

            PrefabUtility.UnpackPrefabInstance(go, mode, InteractionMode.AutomatedAction);

            Undo.CollapseUndoOperations(undoGroup);

            return new JObject
            {
                ["action"] = "unpack",
                ["game_object_path"] = GameObjectResolver.GetHierarchyPath(go),
                ["previous_prefab_asset_path"] = assetPath,
                ["completely"] = completely,
            };
        }

        private static object ExecuteGetStatus(JObject parameters)
        {
            var gameObjectPath = RequireGameObjectPath(parameters);
            var go = ResolveSceneGameObject(gameObjectPath);

            var status = PrefabUtility.GetPrefabInstanceStatus(go);
            var statusStr = status switch
            {
                PrefabInstanceStatus.Connected => "connected",
                PrefabInstanceStatus.Disconnected => "disconnected",
                PrefabInstanceStatus.MissingAsset => "missing_asset",
                PrefabInstanceStatus.NotAPrefab => "not_a_prefab",
                _ => "unknown",
            };

            var result = new JObject
            {
                ["action"] = "get_status",
                ["game_object_path"] = gameObjectPath,
                ["status"] = statusStr,
            };

            if (status != PrefabInstanceStatus.NotAPrefab)
            {
                var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    result["prefab_asset_path"] = assetPath;
                }

                // includeDefaultOverrides=false で実際のオーバーライドのみ検出
                result["has_overrides"] = PrefabUtility.HasPrefabInstanceAnyOverrides(go, false);

                var assetType = PrefabUtility.GetPrefabAssetType(go);
                result["is_variant"] = assetType == PrefabAssetType.Variant;

                if (assetType == PrefabAssetType.Variant)
                {
                    var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (source != null)
                    {
                        var parentPath = AssetDatabase.GetAssetPath(source);
                        if (!string.IsNullOrEmpty(parentPath))
                        {
                            result["parent_prefab_path"] = parentPath;
                        }
                    }
                }
            }

            return result;
        }

        private static string RequireGameObjectPath(JObject parameters)
        {
            var path = Payload.GetString(parameters, "game_object_path");
            if (string.IsNullOrEmpty(path))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    "game_object_path is required");
            }
            return path;
        }

        private static GameObject ResolveSceneGameObject(string path)
        {
            var go = GameObjectResolver.Resolve(path);
            if (go == null)
            {
                throw new PluginException(SceneToolErrors.ObjectNotFound,
                    $"GameObject not found: {path}");
            }
            return go;
        }

        /// <summary>
        /// apply/unpack の事前チェック：対象がプレハブインスタンスであることを検証する。
        /// </summary>
        private static void EnsurePrefabInstance(GameObject go, string gameObjectPath)
        {
            var status = PrefabUtility.GetPrefabInstanceStatus(go);
            if (status == PrefabInstanceStatus.NotAPrefab)
            {
                throw new PluginException(PrefabToolErrors.NotPrefabInstance,
                    $"GameObject is not a prefab instance: {gameObjectPath}");
            }
        }

        /// <summary>
        /// AssetDatabase を使って親フォルダを再帰的に作成する。
        /// </summary>
        private static void EnsureDirectoryExists(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var parent = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                EnsureDirectoryExists(parent);
            }

            var folderName = System.IO.Path.GetFileName(path);
            AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}
