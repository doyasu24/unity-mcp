using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityMcpPlugin.Tools;

namespace UnityMcpPlugin
{
    internal sealed class CommandExecutor
    {
        private const int DefaultRunTestsTimeoutMs = 300_000;
        private const int RetrieveTestListTimeoutMs = 5_000;

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
                var compilationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var compilationStarted = false;

                Action<object> onStarted = _ => { compilationStarted = true; };
                Action<object> onFinished = null;
                onFinished = _ =>
                {
                    CompilationPipeline.compilationStarted -= onStarted;
                    CompilationPipeline.compilationFinished -= onFinished;
                    compilationTcs.TrySetResult(true);
                };

                await MainThreadDispatcher.InvokeAsync(() =>
                {
                    CompilationPipeline.compilationStarted += onStarted;
                    CompilationPipeline.compilationFinished += onFinished;
                    try
                    {
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    }
                    catch
                    {
                        CompilationPipeline.compilationStarted -= onStarted;
                        CompilationPipeline.compilationFinished -= onFinished;
                        throw;
                    }

                    compilationStarted = compilationStarted || EditorApplication.isCompiling;
                    if (!compilationStarted)
                    {
                        CompilationPipeline.compilationStarted -= onStarted;
                        CompilationPipeline.compilationFinished -= onFinished;
                        compilationTcs.TrySetResult(true);
                    }
                    return true;
                });

                await compilationTcs.Task;
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

            if (string.Equals(toolName, ToolNames.RunTests, StringComparison.Ordinal))
            {
                var mode = Payload.GetString(parameters, "mode") ?? RunTestsModes.All;
                if (!RunTestsModes.IsSupported(mode))
                {
                    throw new PluginException(
                        "ERR_INVALID_PARAMS",
                        $"mode must be {RunTestsModes.All}|{RunTestsModes.Edit}|{RunTestsModes.Play}");
                }

                var testFullName = Payload.GetString(parameters, "test_full_name") ?? string.Empty;
                var testNamePattern = Payload.GetString(parameters, "test_name_pattern") ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(testFullName) && !string.IsNullOrWhiteSpace(testNamePattern))
                {
                    throw new PluginException(
                        "ERR_INVALID_PARAMS",
                        "test_full_name and test_name_pattern are mutually exclusive");
                }

                var timeoutMs = Payload.GetInt(parameters, "timeout_ms");
                if (!timeoutMs.HasValue || timeoutMs.Value <= 0)
                {
                    timeoutMs = DefaultRunTestsTimeoutMs;
                }

                return await ExecuteRunTestsAsync(mode, testFullName, testNamePattern, timeoutMs.Value);
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

        private async Task<RunTestsJobResult> ExecuteRunTestsAsync(string mode, string testFullName, string testNamePattern, int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var cancellationToken = cts.Token;

            try
            {
                var aggregate = new RunAggregation(mode, testFullName, testNamePattern);

                if (string.Equals(mode, RunTestsModes.All, StringComparison.Ordinal))
                {
                    await RunSingleModeAsync(TestMode.EditMode, testFullName, testNamePattern, aggregate, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await RunSingleModeAsync(TestMode.PlayMode, testFullName, testNamePattern, aggregate, cancellationToken);
                }
                else
                {
                    var testMode = string.Equals(mode, RunTestsModes.Play, StringComparison.Ordinal)
                        ? TestMode.PlayMode
                        : TestMode.EditMode;
                    await RunSingleModeAsync(testMode, testFullName, testNamePattern, aggregate, cancellationToken);
                }

                return aggregate.ToResult();
            }
            catch (OperationCanceledException)
            {
                throw new PluginException("ERR_TIMEOUT", "run_tests timed out");
            }
            catch (PluginException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return BuildExceptionResult(mode, testFullName, testNamePattern, ex);
            }
        }

        private static async Task RunSingleModeAsync(
            TestMode testMode,
            string testFullName,
            string testNamePattern,
            RunAggregation aggregate,
            CancellationToken cancellationToken)
        {
            // Validate regex upfront so invalid patterns produce a clear error
            // instead of silently returning empty results.
            if (!string.IsNullOrWhiteSpace(testNamePattern))
            {
                try
                {
                    _ = new System.Text.RegularExpressions.Regex(testNamePattern);
                }
                catch (System.ArgumentException ex)
                {
                    throw new PluginException("ERR_INVALID_PARAMS",
                        $"test_name_pattern is not a valid regex: {ex.Message}");
                }
            }

            // Pre-check: retrieve test list and skip Execute if no matching leaf tests exist.
            // TestRunnerApi.Execute() never fires RunFinished when there are no tests,
            // causing the request to hang forever.
            var testListTcs = new TaskCompletionSource<ITestAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);
            TestRunnerApi preCheckApi = null;

            await MainThreadDispatcher.InvokeAsync(() =>
            {
                preCheckApi = ScriptableObject.CreateInstance<TestRunnerApi>();
                preCheckApi.RetrieveTestList(testMode, root => testListTcs.TrySetResult(root));
                return true;
            });

            using var preCheckCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preCheckCts.CancelAfter(RetrieveTestListTimeoutMs);

            var preCheckCancel = Task.Delay(Timeout.Infinite, preCheckCts.Token);
            var preCheckCompleted = await Task.WhenAny(testListTcs.Task, preCheckCancel);

            await MainThreadDispatcher.InvokeAsync(() =>
            {
                if (preCheckApi != null) UnityEngine.Object.DestroyImmediate(preCheckApi);
                return true;
            });

            if (!ReferenceEquals(preCheckCompleted, testListTcs.Task))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            var testRoot = await testListTcs.Task;
            if (!HasMatchingLeafTests(testRoot, testFullName, testNamePattern))
            {
                return;
            }

            // Actual test execution
            var completion = new TaskCompletionSource<ITestResultAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callback = new RunCallback(completion);
            TestRunnerApi testApi = null;

            var runGuid = await MainThreadDispatcher.InvokeAsync(() =>
            {
                testApi = ScriptableObject.CreateInstance<TestRunnerApi>();
                testApi.RegisterCallbacks(callback);

                var testFilter = new Filter
                {
                    testMode = testMode,
                };

                // testFullName → exact match via testNames, testNamePattern → regex via groupNames
                if (!string.IsNullOrWhiteSpace(testFullName))
                {
                    testFilter.testNames = new[] { testFullName };
                }
                else if (!string.IsNullOrWhiteSpace(testNamePattern))
                {
                    testFilter.groupNames = new[] { testNamePattern };
                }

                var settings = new ExecutionSettings(testFilter)
                {
                    runSynchronously = false,
                };

                var guid = testApi.Execute(settings);
                return guid;
            });

            using var registration = cancellationToken.Register(() => RequestCancelRun(runGuid));

            ITestResultAdaptor root;
            try
            {
                var canceledRun = Task.Delay(Timeout.Infinite, cancellationToken);
                var completedRun = await Task.WhenAny(completion.Task, canceledRun);
                if (!ReferenceEquals(completedRun, completion.Task))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                root = await completion.Task;
            }
            finally
            {
                await MainThreadDispatcher.InvokeAsync(() =>
                {
                    TestRunnerApi.UnregisterTestCallback(callback);
                    if (testApi != null)
                    {
                        UnityEngine.Object.DestroyImmediate(testApi);
                    }

                    return true;
                });
            }

            MergeRunResult(root, aggregate);
        }

        /// <summary>
        /// Checks whether the test tree contains leaf tests matching the filter criteria.
        /// When no filter is specified, returns true if any leaf test exists.
        /// When testFullName is specified, matches FullName exactly.
        /// When testNamePattern is specified, matches FullName by regex.
        /// </summary>
        private static bool HasMatchingLeafTests(ITestAdaptor node, string testFullName, string testNamePattern)
        {
            if (node == null) return false;

            if (!node.IsSuite)
            {
                // Leaf test node — check against filter
                if (!string.IsNullOrWhiteSpace(testFullName))
                {
                    return string.Equals(node.FullName, testFullName, StringComparison.Ordinal);
                }

                if (!string.IsNullOrWhiteSpace(testNamePattern))
                {
                    try
                    {
                        return System.Text.RegularExpressions.Regex.IsMatch(
                            node.FullName ?? string.Empty, testNamePattern);
                    }
                    catch (System.ArgumentException)
                    {
                        return false;
                    }
                }

                return true;
            }

            if (node.Children == null) return false;

            foreach (var child in node.Children)
            {
                if (HasMatchingLeafTests(child, testFullName, testNamePattern)) return true;
            }

            return false;
        }

        private static void RequestCancelRun(string runGuid)
        {
            if (string.IsNullOrEmpty(runGuid)) return;
            _ = MainThreadDispatcher.InvokeAsync(() => TestRunnerApi.CancelTestRun(runGuid));
        }

        private static void MergeRunResult(ITestResultAdaptor result, RunAggregation aggregate)
        {
            aggregate.Total += result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
            aggregate.Passed += result.PassCount;
            aggregate.Failed += result.FailCount;
            aggregate.Skipped += result.SkipCount + result.InconclusiveCount;
            aggregate.DurationMs += (int)Math.Round(result.Duration * 1000.0, MidpointRounding.AwayFromZero);

            CollectFailedLeafResults(result, aggregate.FailedTests);
        }

        private static void CollectFailedLeafResults(ITestResultAdaptor result, List<FailedTest> failures)
        {
            if (result == null) return;

            if (result.HasChildren)
            {
                foreach (var child in result.Children)
                {
                    CollectFailedLeafResults(child, failures);
                }

                return;
            }

            if (result.TestStatus != TestStatus.Failed) return;

            failures.Add(new FailedTest(
                result.FullName ?? result.Name ?? "unknown",
                result.Message ?? string.Empty,
                result.StackTrace ?? string.Empty));
        }

        private static RunTestsJobResult BuildExceptionResult(string mode, string testFullName, string testNamePattern, Exception ex)
        {
            return new RunTestsJobResult(
                new TestSummary(1, 0, 1, 0, 0),
                new List<FailedTest>
                {
                    new FailedTest(
                        "run_tests",
                        ex.Message,
                        ex.StackTrace ?? string.Empty),
                },
                mode,
                testFullName,
                testNamePattern);
        }

        private sealed class RunAggregation
        {
            internal RunAggregation(string mode, string testFullName, string testNamePattern)
            {
                Mode = mode;
                TestFullName = testFullName;
                TestNamePattern = testNamePattern;
            }

            internal string Mode { get; }
            internal string TestFullName { get; }
            internal string TestNamePattern { get; }
            internal int Total { get; set; }
            internal int Passed { get; set; }
            internal int Failed { get; set; }
            internal int Skipped { get; set; }
            internal int DurationMs { get; set; }
            internal List<FailedTest> FailedTests { get; } = new();

            internal RunTestsJobResult ToResult()
            {
                return new RunTestsJobResult(
                    new TestSummary(Total, Passed, Failed, Skipped, DurationMs),
                    FailedTests,
                    Mode,
                    TestFullName,
                    TestNamePattern);
            }
        }

        private sealed class RunCallback : ICallbacks
        {
            internal RunCallback(TaskCompletionSource<ITestResultAdaptor> completion)
            {
                Completion = completion;
            }

            internal TaskCompletionSource<ITestResultAdaptor> Completion { get; }

            public void RunStarted(ITestAdaptor testsToRun) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                Completion.TrySetResult(result);
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result) { }
        }
    }
}
