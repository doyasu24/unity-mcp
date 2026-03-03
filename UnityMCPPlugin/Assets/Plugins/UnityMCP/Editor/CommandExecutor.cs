using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityMcpPlugin.Tools;

namespace UnityMcpPlugin
{
    internal sealed class CommandExecutor
    {
        private readonly Func<EditorSnapshot> _snapshotProvider;

        internal CommandExecutor(Func<EditorSnapshot> snapshotProvider)
        {
            _snapshotProvider = snapshotProvider;
        }

        internal async Task<object> ExecuteToolAsync(string toolName, JObject parameters)
        {
            if (string.Equals(toolName, ToolNames.ReadConsole, StringComparison.Ordinal))
            {
                var maxEntries = Payload.GetInt(parameters, "max_entries") ?? ToolLimits.ReadConsoleDefaultMaxEntries;
                if (maxEntries < ToolLimits.ReadConsoleMinEntries || maxEntries > ToolLimits.ReadConsoleMaxEntries)
                {
                    throw new PluginException(
                        "ERR_INVALID_PARAMS",
                        $"max_entries must be {ToolLimits.ReadConsoleMinEntries}..{ToolLimits.ReadConsoleMaxEntries}");
                }

                HashSet<string> logTypes = null;
                if (parameters?["log_type"] is Newtonsoft.Json.Linq.JArray logTypeArray && logTypeArray.Count > 0)
                {
                    logTypes = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var token in logTypeArray)
                    {
                        var val = token?.Value<string>();
                        if (!string.IsNullOrEmpty(val))
                        {
                            if (!ConsoleLogTypes.IsSupported(val))
                            {
                                throw new PluginException("ERR_INVALID_PARAMS", $"Invalid log_type: {val}");
                            }

                            logTypes.Add(val);
                        }
                    }
                }

                System.Text.RegularExpressions.Regex messageRegex = null;
                var messagePattern = Payload.GetString(parameters, "message_pattern");
                if (messagePattern != null)
                {
                    try
                    {
                        messageRegex = new System.Text.RegularExpressions.Regex(
                            messagePattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    }
                    catch (System.ArgumentException)
                    {
                        throw new PluginException("ERR_INVALID_PARAMS", $"Invalid message_pattern regex: {messagePattern}");
                    }
                }

                var stackTraceLines = Payload.GetInt(parameters, "stack_trace_lines") ?? ToolLimits.ReadConsoleDefaultStackTraceLines;
                if (stackTraceLines < 0)
                {
                    throw new PluginException("ERR_INVALID_PARAMS", "stack_trace_lines must be >= 0");
                }

                var deduplicate = Payload.GetBool(parameters, "deduplicate") ?? true;
                var offset = Payload.GetInt(parameters, "offset") ?? 0;
                if (offset < 0)
                {
                    throw new PluginException("ERR_INVALID_PARAMS", "offset must be >= 0");
                }

                return LogBuffer.Read(maxEntries, logTypes, messageRegex, stackTraceLines, deduplicate, offset);
            }

            if (string.Equals(toolName, ToolNames.GetEditorState, StringComparison.Ordinal))
            {
                var snapshot = _snapshotProvider();
                return new RuntimeStatePayload(
                    snapshot.Connected ? "ready" : "waiting_editor",
                    Wire.ToWireState(snapshot.State),
                    snapshot.Connected,
                    snapshot.Seq);
            }

            if (string.Equals(toolName, ToolNames.GetPlayModeState, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(BuildPlayModeStatePayload);
            }

            if (string.Equals(toolName, ToolNames.ClearConsole, StringComparison.Ordinal))
            {
                var clearedCount = LogBuffer.Clear();
                await MainThreadDispatcher.InvokeAsync(() =>
                {
                    if (!TryClearUnityConsole())
                    {
                        throw new PluginException("ERR_UNITY_EXECUTION", "failed to clear Unity Console");
                    }

                    return true;
                });

                return new ClearConsolePayload(true, clearedCount);
            }

            if (string.Equals(toolName, ToolNames.RefreshAssets, StringComparison.Ordinal))
            {
                await MainThreadDispatcher.InvokeAsync(() =>
                {
                    AssetDatabase.Refresh();
                    return true;
                });

                return new RefreshAssetsPayload(true);
            }

            if (string.Equals(toolName, ToolNames.ControlPlayMode, StringComparison.Ordinal))
            {
                var action = Payload.GetString(parameters, "action");
                if (!PlayModeActions.IsSupported(action))
                {
                    throw new PluginException(
                        "ERR_INVALID_PARAMS",
                        $"action must be {PlayModeActions.Start}|{PlayModeActions.Stop}|{PlayModeActions.Pause}");
                }

                return await MainThreadDispatcher.InvokeAsync(() => ControlPlayMode(action!));
            }

            if (string.Equals(toolName, ToolNames.GetSceneHierarchy, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => SceneHierarchyTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.GetSceneComponentInfo, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ComponentInfoTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.ManageSceneComponent, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ManageComponentTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.GetPrefabHierarchy, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => PrefabHierarchyTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.GetPrefabComponentInfo, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => PrefabComponentInfoTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.ManagePrefabComponent, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ManagePrefabComponentTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.ManageSceneGameObject, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ManageGameObjectTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.ManagePrefabGameObject, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ManagePrefabGameObjectTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.ListScenes, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ExecuteListScenes(parameters));
            }

            if (string.Equals(toolName, ToolNames.OpenScene, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ExecuteOpenScene(parameters));
            }

            if (string.Equals(toolName, ToolNames.SaveScene, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ExecuteSaveScene(parameters));
            }

            if (string.Equals(toolName, ToolNames.CreateScene, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ExecuteCreateScene(parameters));
            }

            if (string.Equals(toolName, ToolNames.FindAssets, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ExecuteFindAssets(parameters));
            }

            if (string.Equals(toolName, ToolNames.GetSelection, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(ExecuteGetSelection);
            }

            if (string.Equals(toolName, ToolNames.SetSelection, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ExecuteSetSelection(parameters));
            }

            if (string.Equals(toolName, ToolNames.FindSceneGameObjects, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => FindGameObjectsTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.InstantiatePrefab, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => InstantiatePrefabTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.GetAssetInfo, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => GetAssetInfoTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.FindPrefabGameObjects, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => FindPrefabGameObjectsTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.ManageAsset, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => ManageAssetTool.Execute(parameters));
            }

            if (string.Equals(toolName, ToolNames.CaptureScreenshot, StringComparison.Ordinal))
            {
                return await MainThreadDispatcher.InvokeAsync(() => CaptureScreenshotTool.Execute(parameters));
            }

            throw new PluginException("ERR_UNKNOWN_COMMAND", $"unsupported tool: {toolName}");
        }

        private static ListScenesPayload ExecuteListScenes(JObject parameters)
        {
            var guids = AssetDatabase.FindAssets("t:Scene");
            var allPaths = new List<string>(guids.Length);
            foreach (var guid in guids)
            {
                allPaths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }

            var namePattern = Payload.GetString(parameters, "name_pattern");
            System.Text.RegularExpressions.Regex nameRegex = null;
            if (namePattern != null)
            {
                try
                {
                    nameRegex = new System.Text.RegularExpressions.Regex(
                        namePattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                catch (System.ArgumentException)
                {
                    throw new PluginException("ERR_INVALID_PARAMS", $"Invalid name_pattern regex: {namePattern}");
                }
            }

            var filtered = new List<string>();
            foreach (var path in allPaths)
            {
                if (nameRegex != null)
                {
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (!nameRegex.IsMatch(fileName)) continue;
                }

                filtered.Add(path);
            }

            filtered.Sort(System.StringComparer.Ordinal);

            var maxResults = Payload.GetInt(parameters, "max_results") ?? ListScenesLimits.MaxResultsDefault;
            if (maxResults < 1)
            {
                maxResults = 1;
            }
            else if (maxResults > ListScenesLimits.MaxResultsMax)
            {
                maxResults = ListScenesLimits.MaxResultsMax;
            }

            var offset = Payload.GetInt(parameters, "offset") ?? 0;
            if (offset < 0) offset = 0;

            var totalCount = filtered.Count;
            var startIndex = System.Math.Min(offset, totalCount);
            var endIndex = System.Math.Min(startIndex + maxResults, totalCount);
            var scenes = new List<SceneEntry>(endIndex - startIndex);
            for (var i = startIndex; i < endIndex; i++)
            {
                scenes.Add(new SceneEntry(filtered[i]));
            }

            var truncated = endIndex < totalCount;
            int? nextOffset = truncated ? endIndex : null;
            return new ListScenesPayload(scenes, scenes.Count, totalCount, truncated, nextOffset);
        }

        private static OpenScenePayload ExecuteOpenScene(JObject parameters)
        {
            var path = Payload.GetString(parameters, "path");
            if (string.IsNullOrEmpty(path))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "path is required");
            }

            var modeStr = Payload.GetString(parameters, "mode") ?? OpenSceneModes.Single;
            if (!OpenSceneModes.IsSupported(modeStr))
            {
                throw new PluginException("ERR_INVALID_PARAMS", $"mode must be {OpenSceneModes.Single}|{OpenSceneModes.Additive}");
            }

            var activeScene = SceneManager.GetActiveScene();
            if (activeScene.isDirty)
            {
                throw new PluginException("ERR_UNSAVED_CHANGES",
                    "The current scene has unsaved changes. Call save_scene before opening a new scene.");
            }

            var openMode = modeStr == OpenSceneModes.Additive
                ? OpenSceneMode.Additive
                : OpenSceneMode.Single;

            EditorSceneManager.OpenScene(path, openMode);
            return new OpenScenePayload(path, modeStr);
        }

        private static SaveScenePayload ExecuteSaveScene(JObject parameters)
        {
            var path = Payload.GetString(parameters, "path");
            if (!string.IsNullOrEmpty(path))
            {
                Scene targetScene = default;
                var found = false;
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.path == path)
                    {
                        targetScene = s;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    throw new PluginException("ERR_OBJECT_NOT_FOUND", $"No open scene found with path: {path}");
                }

                EditorSceneManager.SaveScene(targetScene);
                return new SaveScenePayload(targetScene.path);
            }

            var activeScene = SceneManager.GetActiveScene();
            EditorSceneManager.SaveScene(activeScene);
            return new SaveScenePayload(activeScene.path);
        }

        private static CreateScenePayload ExecuteCreateScene(JObject parameters)
        {
            var path = Payload.GetString(parameters, "path");
            if (string.IsNullOrEmpty(path))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "path is required");
            }

            var setupStr = Payload.GetString(parameters, "setup") ?? CreateSceneSetups.Default;
            if (!CreateSceneSetups.IsSupported(setupStr))
            {
                throw new PluginException("ERR_INVALID_PARAMS", $"setup must be {CreateSceneSetups.Default}|{CreateSceneSetups.Empty}");
            }

            var setup = setupStr == CreateSceneSetups.Empty
                ? NewSceneSetup.EmptyScene
                : NewSceneSetup.DefaultGameObjects;

            var scene = EditorSceneManager.NewScene(setup, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, path);
            return new CreateScenePayload(path);
        }

        private static FindAssetsPayload ExecuteFindAssets(JObject parameters)
        {
            var filter = Payload.GetString(parameters, "filter");
            if (string.IsNullOrEmpty(filter))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "filter is required");
            }

            var maxResults = Payload.GetInt(parameters, "max_results") ?? FindAssetsLimits.MaxResultsDefault;
            if (maxResults < 1)
            {
                maxResults = 1;
            }
            else if (maxResults > FindAssetsLimits.MaxResultsMax)
            {
                maxResults = FindAssetsLimits.MaxResultsMax;
            }

            var offset = Payload.GetInt(parameters, "offset") ?? 0;

            string[] guids;
            if (parameters["search_in_folders"] is JArray foldersArray && foldersArray.Count > 0)
            {
                var folders = new List<string>(foldersArray.Count);
                foreach (var token in foldersArray)
                {
                    var folder = token?.Value<string>();
                    if (!string.IsNullOrEmpty(folder))
                    {
                        folders.Add(folder);
                    }
                }

                guids = AssetDatabase.FindAssets(filter, folders.ToArray());
            }
            else
            {
                guids = AssetDatabase.FindAssets(filter);
            }

            // Resolve paths and sort for stable pagination order
            var allEntries = new List<AssetEntry>(guids.Length);
            foreach (var guid in guids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                var typeName = assetType != null ? assetType.Name : "Unknown";
                var fileName = System.IO.Path.GetFileName(assetPath);
                allEntries.Add(new AssetEntry(assetPath, typeName, fileName, guid));
            }

            allEntries.Sort((a, b) => string.Compare(a.Path, b.Path, System.StringComparison.Ordinal));

            var totalCount = allEntries.Count;
            var startIndex = System.Math.Min(offset, totalCount);
            var endIndex = System.Math.Min(startIndex + maxResults, totalCount);
            var assets = allEntries.GetRange(startIndex, endIndex - startIndex);

            var truncated = endIndex < totalCount;
            int? nextOffset = truncated ? endIndex : null;
            return new FindAssetsPayload(assets, assets.Count, truncated, totalCount, nextOffset);
        }

        private static GetSelectionPayload ExecuteGetSelection()
        {
            var activeObj = Selection.activeObject;
            SelectedObjectInfo activeInfo = null;
            if (activeObj != null)
            {
                activeInfo = BuildSelectedObjectInfo(activeObj);
            }

            var selectedObjects = Selection.objects;
            var selectedInfos = new List<SelectedObjectInfo>(selectedObjects.Length);
            foreach (var obj in selectedObjects)
            {
                if (obj != null)
                {
                    selectedInfos.Add(BuildSelectedObjectInfo(obj));
                }
            }

            return new GetSelectionPayload(activeInfo, selectedInfos, selectedInfos.Count);
        }

        private static SelectedObjectInfo BuildSelectedObjectInfo(UnityEngine.Object obj)
        {
            var go = obj as GameObject;
            string path;
            if (go != null)
            {
                path = GameObjectResolver.GetHierarchyPath(go);
            }
            else
            {
                path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path))
                {
                    path = "";
                }
            }

            return new SelectedObjectInfo(obj.name, obj.GetInstanceID(), obj.GetType().Name, path);
        }

        private static SetSelectionPayload ExecuteSetSelection(JObject parameters)
        {
            var objects = new List<UnityEngine.Object>();

            if (parameters["paths"] is JArray pathsArray)
            {
                foreach (var token in pathsArray)
                {
                    var p = token?.Value<string>();
                    if (string.IsNullOrEmpty(p))
                    {
                        continue;
                    }

                    var go = GameObjectResolver.Resolve(p);
                    if (go != null)
                    {
                        objects.Add(go);
                        continue;
                    }

                    var asset = AssetDatabase.LoadMainAssetAtPath(p);
                    if (asset != null)
                    {
                        objects.Add(asset);
                    }
                }
            }

            if (parameters["instance_ids"] is JArray idsArray)
            {
                foreach (var token in idsArray)
                {
                    var id = token?.Value<int>() ?? 0;
                    if (id == 0)
                    {
                        continue;
                    }

                    #pragma warning disable CS0618
                    var obj = EditorUtility.InstanceIDToObject(id);
                    #pragma warning restore CS0618
                    if (obj != null)
                    {
                        objects.Add(obj);
                    }
                }
            }

            Selection.objects = objects.ToArray();
            return new SetSelectionPayload(true, objects.Count);
        }

        private static PlayModeControlPayload ControlPlayMode(string action)
        {
            switch (action)
            {
                case PlayModeActions.Start:
                    EditorApplication.isPaused = false;
                    EditorApplication.isPlaying = true;
                    break;
                case PlayModeActions.Stop:
                    EditorApplication.isPaused = false;
                    EditorApplication.isPlaying = false;
                    break;
                case PlayModeActions.Pause:
                    if (!EditorApplication.isPlaying)
                    {
                        throw new PluginException("ERR_INVALID_STATE", "pause requires play mode");
                    }

                    EditorApplication.isPaused = true;
                    break;
                default:
                    throw new PluginException(
                        "ERR_INVALID_PARAMS",
                        $"action must be {PlayModeActions.Start}|{PlayModeActions.Stop}|{PlayModeActions.Pause}");
            }

            return BuildPlayModePayload(action);
        }

        private static PlayModeStatePayload BuildPlayModeStatePayload()
        {
            var isPlaying = EditorApplication.isPlaying;
            var isPaused = EditorApplication.isPaused;
            var isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
            var state = isPlaying
                ? (isPaused ? PlayModeStates.Paused : PlayModeStates.Playing)
                : PlayModeStates.Stopped;

            return new PlayModeStatePayload(
                state,
                isPlaying,
                isPaused,
                isPlayingOrWillChangePlaymode);
        }

        private static PlayModeControlPayload BuildPlayModePayload(string action)
        {
            return new PlayModeControlPayload(
                action,
                true,
                EditorApplication.isPlaying,
                EditorApplication.isPaused,
                EditorApplication.isPlayingOrWillChangePlaymode);
        }

        private static bool TryClearUnityConsole()
        {
            return TryInvokeClear("UnityEditor.LogEntries, UnityEditor") ||
                   TryInvokeClear("UnityEditorInternal.LogEntries, UnityEditor");
        }

        private static bool TryInvokeClear(string typeName)
        {
            var type = Type.GetType(typeName);
            if (type == null)
            {
                return false;
            }

            var clearMethod = type.GetMethod("Clear", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (clearMethod == null)
            {
                return false;
            }

            clearMethod.Invoke(null, null);
            return true;
        }
    }
}
