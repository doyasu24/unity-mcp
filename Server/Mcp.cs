using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace UnityMcpServer;

internal sealed class McpToolService
{
    private readonly RuntimeState _runtimeState;
    private readonly UnityBridge _unityBridge;
    private readonly ILogger<McpToolService> _logger;

    public McpToolService(RuntimeState runtimeState, UnityBridge unityBridge, ILogger<McpToolService> logger)
    {
        _runtimeState = runtimeState;
        _unityBridge = unityBridge;
        _logger = logger;
    }

    public async Task<JsonObject> CallToolAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await ExecuteToolAsync(toolName, arguments, cancellationToken);

            // スクリーンショットは base64 が大きいため結果ログを省略
            if (string.Equals(toolName, ToolNames.CaptureScreenshot, StringComparison.Ordinal))
            {
                _logger.ZLogInformation($"{toolName} {arguments} → (screenshot)");
                return FormatScreenshotResult(payload);
            }

            _logger.ZLogInformation($"{toolName} {arguments} → {payload}");
            return ToolResultFormatter.Success(payload);
        }
        catch (McpException ex)
        {
            _logger.ZLogWarning($"{toolName} {arguments} → {ex.Code}: {ex.Message}");
            return ToolResultFormatter.Error(ex);
        }
        catch (Exception ex)
        {
            _logger.ZLogError(ex, $"{toolName} {arguments} → {ex.Message}");
            return ToolResultFormatter.Error(new McpException(
                ErrorCodes.UnityExecution,
                "Unexpected server error",
                new JsonObject { ["message"] = ex.Message }));
        }
    }

    private const int MaxInlineImageBytes = 5 * 1024 * 1024; // 5 MB

    internal static JsonObject FormatScreenshotResult(object payload)
    {
        var structured = ToolResultFormatter.ToStructuredContent(payload);
        var filePath = (structured as JsonObject)?["file_path"]?.GetValue<string>();

        if (string.IsNullOrEmpty(filePath) ||
            !filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(filePath))
        {
            return ToolResultFormatter.Success(payload);
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxInlineImageBytes)
            {
                return ToolResultFormatter.Success(payload);
            }

            var pngBytes = File.ReadAllBytes(filePath);
            var base64 = Convert.ToBase64String(pngBytes);
            return ToolResultFormatter.SuccessWithImage(structured, base64, "image/png");
        }
        catch
        {
            return ToolResultFormatter.Success(payload);
        }
    }

    private async Task<object> ExecuteToolAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        return toolName switch
        {
            ToolNames.GetEditorState => _runtimeState.GetSnapshot(),
            ToolNames.GetPlayModeState => (await _unityBridge.GetPlayModeStateAsync(cancellationToken)).Payload,
            ToolNames.ReadConsole => (await _unityBridge.ReadConsoleAsync(ParseReadConsoleRequest(arguments), cancellationToken)).Payload,
            ToolNames.ClearConsole => (await _unityBridge.ClearConsoleAsync(cancellationToken)).Payload,
            ToolNames.RefreshAssets => (await _unityBridge.RefreshAssetsAsync(cancellationToken)).Payload,
            ToolNames.ControlPlayMode => (await _unityBridge.ControlPlayModeAsync(ParseControlPlayModeRequest(arguments), cancellationToken)).Payload,
            ToolNames.RunTests => (await _unityBridge.RunTestsAsync(ParseRunTestsRequest(arguments), cancellationToken)).Payload,
            ToolNames.GetHierarchy => await ExecuteGetHierarchyAsync(arguments, cancellationToken),
            ToolNames.GetComponentInfo => await ExecuteGetComponentInfoAsync(arguments, cancellationToken),
            ToolNames.ManageComponent => await ExecuteManageComponentAsync(arguments, cancellationToken),
            ToolNames.FindGameObjects => await ExecuteFindGameObjectsAsync(arguments, cancellationToken),
            ToolNames.ManageGameObject => await ExecuteManageGameObjectAsync(arguments, cancellationToken),
            ToolNames.ListScenes => (await _unityBridge.ListScenesAsync(ParseListScenesRequest(arguments), cancellationToken)).Payload,
            ToolNames.OpenScene => (await _unityBridge.OpenSceneAsync(ParseOpenSceneRequest(arguments), cancellationToken)).Payload,
            ToolNames.SaveScene => (await _unityBridge.SaveSceneAsync(ParseSaveSceneRequest(arguments), cancellationToken)).Payload,
            ToolNames.CreateScene => (await _unityBridge.CreateSceneAsync(ParseCreateSceneRequest(arguments), cancellationToken)).Payload,
            ToolNames.FindAssets => (await _unityBridge.FindAssetsAsync(ParseFindAssetsRequest(arguments), cancellationToken)).Payload,
            ToolNames.InstantiatePrefab => (await _unityBridge.InstantiatePrefabAsync(ParseInstantiatePrefabRequest(arguments), cancellationToken)).Payload,
            ToolNames.GetAssetInfo => (await _unityBridge.GetAssetInfoAsync(ParseGetAssetInfoRequest(arguments), cancellationToken)).Payload,
            ToolNames.ManageAsset => (await _unityBridge.ManageAssetAsync(ParseManageAssetRequest(arguments), cancellationToken)).Payload,
            ToolNames.CaptureScreenshot => (await _unityBridge.CaptureScreenshotAsync(ParseCaptureScreenshotRequest(arguments), cancellationToken)).Payload,
            ToolNames.ManageAsmdef => (await _unityBridge.ManageAsmdefAsync(ParseManageAsmdefRequest(arguments), cancellationToken)).Payload,
            ToolNames.ManagePrefab => (await _unityBridge.ManagePrefabAsync(ParseManagePrefabRequest(arguments), cancellationToken)).Payload,
            ToolNames.ExecuteBatch => await ExecuteBatchServerSideAsync(arguments, cancellationToken),
            _ => throw new McpException(ErrorCodes.UnknownCommand, $"Unknown tool: {toolName}"),
        };
    }

    /// <summary>
    /// Server-side batch: validates all operations upfront, then dispatches each via ExecuteToolAsync.
    /// Each operation goes through the normal bridge safety path (EnsureEditMode, reconnect handling, etc.).
    /// Operations may be interleaved with other requests (no isolation guarantee).
    /// </summary>
    private async Task<object> ExecuteBatchServerSideAsync(JsonObject arguments, CancellationToken ct)
    {
        // --- Parse & validate ALL operations upfront ---
        var opsArray = arguments["operations"] as JsonArray;
        if (opsArray == null || opsArray.Count == 0)
            throw new McpException(ErrorCodes.InvalidParams,
                "operations is required and must be a non-empty array");
        if (opsArray.Count > ExecuteBatchLimits.MaxOperations)
            throw new McpException(ErrorCodes.InvalidParams,
                $"operations must have at most {ExecuteBatchLimits.MaxOperations} items");

        var validated = new (string ToolName, JsonObject Args)[opsArray.Count];
        for (int i = 0; i < opsArray.Count; i++)
        {
            if (opsArray[i] is not JsonObject opObj)
                throw new McpException(ErrorCodes.InvalidParams, $"operations[{i}] must be an object");

            var toolName = JsonHelpers.GetString(opObj, "tool_name");
            if (string.IsNullOrWhiteSpace(toolName))
                throw new McpException(ErrorCodes.InvalidParams, $"operations[{i}].tool_name is required");
            if (toolName == ToolNames.ExecuteBatch)
                throw new McpException(ErrorCodes.InvalidParams,
                    $"operations[{i}].tool_name 'execute_batch' is not allowed in a batch");
            if (!ToolCatalog.Items.ContainsKey(toolName))
                throw new McpException(ErrorCodes.InvalidParams,
                    $"operations[{i}].tool_name '{toolName}' is not a known tool");

            // "as JsonObject" で非オブジェクト型（配列・文字列等）を安全に null 化
            var args = opObj["arguments"] as JsonObject;
            validated[i] = (toolName, args?.DeepClone().AsObject() ?? new JsonObject());
        }

        // --- Execute sequentially ---
        var stopOnError = JsonHelpers.GetBool(arguments, "stop_on_error") ?? true;
        var results = new JsonArray();
        int succeeded = 0, failed = 0, skipped = 0;
        bool stopped = false;

        foreach (var (toolName, args) in validated)
        {
            if (stopped)
            {
                results.Add(BuildOpResult(toolName, false, null, "skipped"));
                skipped++;
                continue;
            }

            try
            {
                var payload = await ExecuteToolAsync(toolName, args, ct);
                results.Add(BuildOpResult(toolName, true, payload, null));
                succeeded++;
            }
            catch (McpException ex)
            {
                results.Add(BuildOpResult(toolName, false, null, $"{ex.Code}: {ex.Message}"));
                failed++;
                if (stopOnError) stopped = true;
            }
            catch (Exception ex)
            {
                results.Add(BuildOpResult(toolName, false, null, ex.Message));
                failed++;
                if (stopOnError) stopped = true;
            }
        }

        // --- Post-batch: リコンパイルを伴うツールが実行された場合、Editor Ready を最終確認 ---
        // 個別操作のリカバリは各 bridge メソッド内で完結するが、
        // バッチ全体の完了時に Editor が Ready であることをバッチレベルで保証する。
        // _runtimeState を直接使い、スケジューラを経由しない（因果的分離を避ける）。
        // stopped == true の場合でも、既に実行された操作がリコンパイルを引き起こしている可能性がある。
        var executedCount = succeeded + failed;
        if (executedCount > 0 && validated.Take(executedCount).Any(v => ToolCatalog.Items[v.ToolName].MayTriggerRecompile))
        {
            try
            {
                if (!_runtimeState.IsEditorReady())
                {
                    var waitPolicy = UnityBridge.ResolveEditorReadyWaitPolicy(_runtimeState.GetSnapshot());
                    await _runtimeState.WaitForEditorReadyAsync(waitPolicy.Timeout, ct);
                }
            }
            catch
            {
                // post-batch ready check は non-fatal
            }
        }

        return new JsonObject
        {
            ["success"] = failed == 0,
            ["results"] = results,
            ["summary"] = new JsonObject
            {
                ["total"] = results.Count,
                ["succeeded"] = succeeded,
                ["failed"] = failed,
                ["skipped"] = skipped,
            },
        };
    }

    private static JsonObject BuildOpResult(string toolName, bool success, object? result, string? error)
    {
        var obj = new JsonObject
        {
            ["tool_name"] = toolName,
            ["success"] = success,
        };
        if (success && result != null)
            obj["result"] = ToolResultFormatter.ToStructuredContent(result);
        if (!success && error != null)
            obj["error"] = error;
        return obj;
    }

    private static ReadConsoleRequest ParseReadConsoleRequest(JsonObject arguments)
    {
        var maxEntries = JsonHelpers.GetInt(arguments, "max_entries") ?? ToolLimits.ReadConsoleDefaultMaxEntries;
        if (maxEntries is < ToolLimits.ReadConsoleMinEntries or > ToolLimits.ReadConsoleMaxEntries)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"max_entries must be between {ToolLimits.ReadConsoleMinEntries} and {ToolLimits.ReadConsoleMaxEntries}",
                new JsonObject { ["max_entries"] = maxEntries });
        }

        string[]? logType = null;
        if (arguments.TryGetPropertyValue("log_type", out var ltNode) && ltNode is JsonArray ltArray && ltArray.Count > 0)
        {
            var types = new List<string>(ltArray.Count);
            foreach (var item in ltArray)
            {
                var t = item?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (!ConsoleLogTypes.IsSupported(t))
                {
                    throw new McpException(ErrorCodes.InvalidParams, $"Invalid log_type: {t}");
                }
                types.Add(t);
            }
            if (types.Count > 0) logType = types.ToArray();
        }

        var messagePattern = JsonHelpers.GetString(arguments, "message_pattern");

        var stackTraceLines = JsonHelpers.GetInt(arguments, "stack_trace_lines") ?? ToolLimits.ReadConsoleDefaultStackTraceLines;
        if (stackTraceLines < 0)
        {
            throw new McpException(ErrorCodes.InvalidParams, "stack_trace_lines must be >= 0");
        }

        var deduplicate = JsonHelpers.GetBool(arguments, "deduplicate") ?? true;

        var offset = JsonHelpers.GetInt(arguments, "offset") ?? 0;
        if (offset < 0)
        {
            throw new McpException(ErrorCodes.InvalidParams, "offset must be >= 0");
        }

        return new ReadConsoleRequest(maxEntries, logType, messagePattern, stackTraceLines, deduplicate, offset);
    }

    private static RunTestsRequest ParseRunTestsRequest(JsonObject arguments)
    {
        var mode = JsonHelpers.GetString(arguments, "mode") ?? RunTestsModes.All;
        if (!RunTestsModes.IsSupported(mode))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"mode must be one of {RunTestsModes.All}|{RunTestsModes.Edit}|{RunTestsModes.Play}",
                new JsonObject { ["mode"] = mode });
        }

        var testFullName = JsonHelpers.GetString(arguments, "test_full_name");
        var testNamePattern = JsonHelpers.GetString(arguments, "test_name_pattern");

        if (!string.IsNullOrWhiteSpace(testFullName) && !string.IsNullOrWhiteSpace(testNamePattern))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                "test_full_name and test_name_pattern are mutually exclusive");
        }

        if (arguments.TryGetPropertyValue("test_full_name", out var fnNode) && fnNode is not null &&
            string.IsNullOrWhiteSpace(testFullName))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                "test_full_name must be a non-empty string when provided");
        }

        if (arguments.TryGetPropertyValue("test_name_pattern", out var pnNode) && pnNode is not null &&
            string.IsNullOrWhiteSpace(testNamePattern))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                "test_name_pattern must be a non-empty string when provided");
        }

        return new RunTestsRequest(mode, testFullName, testNamePattern);
    }

    private static ControlPlayModeRequest ParseControlPlayModeRequest(JsonObject arguments)
    {
        var action = PlayModeActions.Normalize(JsonHelpers.GetString(arguments, "action"));
        if (!PlayModeActions.IsSupported(action))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"action must be one of {PlayModeActions.Start}|{PlayModeActions.Stop}|{PlayModeActions.Pause}",
                new JsonObject { ["action"] = action });
        }

        return new ControlPlayModeRequest(action!);
    }

    private static bool HasPrefabPath(JsonObject arguments)
    {
        return !string.IsNullOrWhiteSpace(JsonHelpers.GetString(arguments, "prefab_path"));
    }

    private async Task<object> ExecuteGetHierarchyAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (HasPrefabPath(arguments))
        {
            return (await _unityBridge.GetPrefabHierarchyAsync(ParseGetPrefabHierarchyRequest(arguments), cancellationToken)).Payload;
        }

        return (await _unityBridge.GetSceneHierarchyAsync(ParseGetSceneHierarchyRequest(arguments), cancellationToken)).Payload;
    }

    private async Task<object> ExecuteGetComponentInfoAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (HasPrefabPath(arguments))
        {
            return (await _unityBridge.GetPrefabComponentInfoAsync(ParseGetPrefabComponentInfoRequest(arguments), cancellationToken)).Payload;
        }

        return (await _unityBridge.GetSceneComponentInfoAsync(ParseGetSceneComponentInfoRequest(arguments), cancellationToken)).Payload;
    }

    private async Task<object> ExecuteManageComponentAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (HasPrefabPath(arguments))
        {
            return (await _unityBridge.ManagePrefabComponentAsync(ParseManagePrefabComponentRequest(arguments), cancellationToken)).Payload;
        }

        return (await _unityBridge.ManageSceneComponentAsync(ParseManageSceneComponentRequest(arguments), cancellationToken)).Payload;
    }

    private async Task<object> ExecuteFindGameObjectsAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (HasPrefabPath(arguments))
        {
            return (await _unityBridge.FindPrefabGameObjectsAsync(ParseFindPrefabGameObjectsRequest(arguments), cancellationToken)).Payload;
        }

        return (await _unityBridge.FindSceneGameObjectsAsync(ParseFindSceneGameObjectsRequest(arguments), cancellationToken)).Payload;
    }

    private async Task<object> ExecuteManageGameObjectAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (HasPrefabPath(arguments))
        {
            return (await _unityBridge.ManagePrefabGameObjectAsync(ParseManagePrefabGameObjectRequest(arguments), cancellationToken)).Payload;
        }

        return (await _unityBridge.ManageSceneGameObjectAsync(ParseManageSceneGameObjectRequest(arguments), cancellationToken)).Payload;
    }

    private static ListScenesRequest ParseListScenesRequest(JsonObject arguments)
    {
        var namePattern = JsonHelpers.GetString(arguments, "name_pattern");

        var maxResults = JsonHelpers.GetInt(arguments, "max_results") ?? ListScenesLimits.MaxResultsDefault;
        if (maxResults is < ListScenesLimits.MaxResultsMin or > ListScenesLimits.MaxResultsMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"max_results must be between {ListScenesLimits.MaxResultsMin} and {ListScenesLimits.MaxResultsMax}",
                new JsonObject { ["max_results"] = maxResults });
        }

        var offset = JsonHelpers.GetInt(arguments, "offset") ?? 0;
        if (offset < 0)
        {
            throw new McpException(ErrorCodes.InvalidParams, "offset must be >= 0",
                new JsonObject { ["offset"] = offset });
        }

        return new ListScenesRequest(namePattern, maxResults, offset);
    }

    private static GetSceneHierarchyRequest ParseGetSceneHierarchyRequest(JsonObject arguments)
    {
        var rootPath = JsonHelpers.GetString(arguments, "root_path");

        var maxDepth = JsonHelpers.GetInt(arguments, "max_depth") ?? SceneToolLimits.MaxDepthDefault;
        if (maxDepth is < SceneToolLimits.MaxDepthMin or > SceneToolLimits.MaxDepthMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"max_depth must be between {SceneToolLimits.MaxDepthMin} and {SceneToolLimits.MaxDepthMax}",
                new JsonObject { ["max_depth"] = maxDepth });
        }

        var maxGameObjects = JsonHelpers.GetInt(arguments, "max_game_objects") ?? SceneToolLimits.MaxGameObjectsDefault;
        if (maxGameObjects is < SceneToolLimits.MaxGameObjectsMin or > SceneToolLimits.MaxGameObjectsMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"max_game_objects must be between {SceneToolLimits.MaxGameObjectsMin} and {SceneToolLimits.MaxGameObjectsMax}",
                new JsonObject { ["max_game_objects"] = maxGameObjects });
        }

        var offset = JsonHelpers.GetInt(arguments, "offset") ?? 0;
        if (offset < 0)
        {
            throw new McpException(ErrorCodes.InvalidParams, "offset must be >= 0",
                new JsonObject { ["offset"] = offset });
        }

        var componentFilter = ParseComponentFilter(arguments);

        return new GetSceneHierarchyRequest(rootPath, maxDepth, maxGameObjects, offset, componentFilter);
    }

    private static GetSceneComponentInfoRequest ParseGetSceneComponentInfoRequest(JsonObject arguments)
    {
        var gameObjectPath = JsonHelpers.GetString(arguments, "game_object_path");
        if (string.IsNullOrWhiteSpace(gameObjectPath))
        {
            throw new McpException(ErrorCodes.InvalidParams, "game_object_path is required");
        }

        var index = JsonHelpers.GetInt(arguments, "index");
        if (index.HasValue && index.Value < 0)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                "index must be >= 0",
                new JsonObject { ["index"] = index.Value });
        }

        string[]? fields = null;
        if (arguments.TryGetPropertyValue("fields", out var fieldsNode) && fieldsNode is JsonArray fieldsArray)
        {
            fields = fieldsArray
                .Select(n => n?.GetValue<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray()!;
        }

        var maxArrayElements = JsonHelpers.GetInt(arguments, "max_array_elements") ?? SceneToolLimits.MaxArrayElementsDefault;
        if (maxArrayElements is < SceneToolLimits.MaxArrayElementsMin or > SceneToolLimits.MaxArrayElementsMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"max_array_elements must be between {SceneToolLimits.MaxArrayElementsMin} and {SceneToolLimits.MaxArrayElementsMax}",
                new JsonObject { ["max_array_elements"] = maxArrayElements });
        }

        return new GetSceneComponentInfoRequest(gameObjectPath, index, fields, maxArrayElements);
    }

    private static ManageSceneComponentRequest ParseManageSceneComponentRequest(JsonObject arguments)
    {
        var action = JsonHelpers.GetString(arguments, "action");
        if (!ManageActions.IsSupported(action))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"action must be one of {ManageActions.Add}|{ManageActions.Update}|{ManageActions.Remove}|{ManageActions.Move}",
                new JsonObject { ["action"] = action });
        }

        var gameObjectPath = JsonHelpers.GetString(arguments, "game_object_path");
        if (string.IsNullOrWhiteSpace(gameObjectPath))
        {
            throw new McpException(ErrorCodes.InvalidParams, "game_object_path is required");
        }

        var componentType = JsonHelpers.GetString(arguments, "component_type");
        var index = JsonHelpers.GetInt(arguments, "index");
        var newIndex = JsonHelpers.GetInt(arguments, "new_index");
        JsonObject? fields = null;
        if (arguments.TryGetPropertyValue("fields", out var fieldsNode) && fieldsNode is JsonObject fieldsObj)
        {
            fields = fieldsObj;
        }

        switch (action)
        {
            case ManageActions.Add:
                if (string.IsNullOrWhiteSpace(componentType))
                {
                    throw new McpException(ErrorCodes.InvalidParams, "component_type is required for 'add' action");
                }

                break;
            case ManageActions.Update:
                if (!index.HasValue)
                {
                    throw new McpException(ErrorCodes.InvalidParams, "index is required for 'update' action");
                }

                if (fields is null)
                {
                    throw new McpException(ErrorCodes.InvalidParams, "fields is required for 'update' action");
                }

                break;
            case ManageActions.Remove:
                if (!index.HasValue)
                {
                    throw new McpException(ErrorCodes.InvalidParams, "index is required for 'remove' action");
                }

                break;
            case ManageActions.Move:
                if (!index.HasValue)
                {
                    throw new McpException(ErrorCodes.InvalidParams, "index is required for 'move' action");
                }

                if (!newIndex.HasValue)
                {
                    throw new McpException(ErrorCodes.InvalidParams, "new_index is required for 'move' action");
                }

                break;
        }

        return new ManageSceneComponentRequest(action!, gameObjectPath, componentType, index, newIndex, fields);
    }

    private static OpenSceneRequest ParseOpenSceneRequest(JsonObject arguments)
    {
        var path = JsonHelpers.GetString(arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException(ErrorCodes.InvalidParams, "path is required");
        }

        var mode = JsonHelpers.GetString(arguments, "mode") ?? OpenSceneModes.Single;
        if (!OpenSceneModes.IsSupported(mode))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"mode must be one of {OpenSceneModes.Single}|{OpenSceneModes.Additive}",
                new JsonObject { ["mode"] = mode });
        }

        return new OpenSceneRequest(path, mode);
    }

    private static SaveSceneRequest ParseSaveSceneRequest(JsonObject arguments)
    {
        var path = JsonHelpers.GetString(arguments, "path");
        return new SaveSceneRequest(path);
    }

    private static CreateSceneRequest ParseCreateSceneRequest(JsonObject arguments)
    {
        var path = JsonHelpers.GetString(arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException(ErrorCodes.InvalidParams, "path is required");
        }

        var setup = JsonHelpers.GetString(arguments, "setup") ?? CreateSceneSetups.Default;
        if (!CreateSceneSetups.IsSupported(setup))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"setup must be one of {CreateSceneSetups.Default}|{CreateSceneSetups.Empty}",
                new JsonObject { ["setup"] = setup });
        }

        return new CreateSceneRequest(path, setup);
    }

    private static FindAssetsRequest ParseFindAssetsRequest(JsonObject arguments)
    {
        var filter = JsonHelpers.GetString(arguments, "filter");
        if (string.IsNullOrWhiteSpace(filter))
        {
            throw new McpException(ErrorCodes.InvalidParams, "filter is required");
        }

        string[]? searchInFolders = null;
        if (arguments.TryGetPropertyValue("search_in_folders", out var foldersNode) && foldersNode is JsonArray foldersArray)
        {
            searchInFolders = foldersArray
                .Select(n => n?.GetValue<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray()!;
        }

        var maxResults = JsonHelpers.GetInt(arguments, "max_results") ?? FindAssetsLimits.MaxResultsDefault;
        if (maxResults is < FindAssetsLimits.MaxResultsMin or > FindAssetsLimits.MaxResultsMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"max_results must be between {FindAssetsLimits.MaxResultsMin} and {FindAssetsLimits.MaxResultsMax}",
                new JsonObject { ["max_results"] = maxResults });
        }

        var offset = JsonHelpers.GetInt(arguments, "offset") ?? 0;
        if (offset < 0)
        {
            throw new McpException(ErrorCodes.InvalidParams, "offset must be >= 0",
                new JsonObject { ["offset"] = offset });
        }

        return new FindAssetsRequest(filter, searchInFolders, maxResults, offset);
    }

    private static FindSceneGameObjectsRequest ParseFindSceneGameObjectsRequest(JsonObject arguments)
    {
        var name = JsonHelpers.GetString(arguments, "name");
        var tag = JsonHelpers.GetString(arguments, "tag");
        var componentType = JsonHelpers.GetString(arguments, "component_type");
        var rootPath = JsonHelpers.GetString(arguments, "root_path");
        var layer = JsonHelpers.GetInt(arguments, "layer");
        bool? active = null;
        if (arguments.TryGetPropertyValue("active", out var activeNode) && activeNode is not null)
        {
            active = activeNode.GetValue<bool>();
        }

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(tag) && string.IsNullOrWhiteSpace(componentType) && !layer.HasValue)
        {
            throw new McpException(ErrorCodes.InvalidParams, "at least one filter (name, tag, component_type, or layer) is required");
        }

        if (layer.HasValue && (layer.Value < 0 || layer.Value > 31))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                "layer must be between 0 and 31",
                new JsonObject { ["layer"] = layer.Value });
        }

        var maxResults = JsonHelpers.GetInt(arguments, "max_results") ?? FindSceneGameObjectsLimits.MaxResultsDefault;
        if (maxResults is < FindSceneGameObjectsLimits.MaxResultsMin or > FindSceneGameObjectsLimits.MaxResultsMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"max_results must be between {FindSceneGameObjectsLimits.MaxResultsMin} and {FindSceneGameObjectsLimits.MaxResultsMax}",
                new JsonObject { ["max_results"] = maxResults });
        }

        var offset = JsonHelpers.GetInt(arguments, "offset") ?? 0;
        if (offset < 0)
        {
            throw new McpException(ErrorCodes.InvalidParams, "offset must be >= 0",
                new JsonObject { ["offset"] = offset });
        }

        return new FindSceneGameObjectsRequest(name, tag, componentType, rootPath, layer, active, maxResults, offset);
    }

    private static string ParseRequiredPrefabPath(JsonObject arguments)
    {
        var prefabPath = JsonHelpers.GetString(arguments, "prefab_path");
        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            throw new McpException(ErrorCodes.InvalidParams, "prefab_path is required");
        }

        return prefabPath;
    }

    private static string[]? ParseComponentFilter(JsonObject arguments)
    {
        if (!arguments.TryGetPropertyValue("component_filter", out var node) || node is not JsonArray arr || arr.Count == 0)
        {
            return null;
        }

        var list = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            var val = item?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(val))
            {
                list.Add(val);
            }
        }

        return list.Count > 0 ? list.ToArray() : null;
    }

    private static GetPrefabHierarchyRequest ParseGetPrefabHierarchyRequest(JsonObject arguments)
    {
        var prefabPath = ParseRequiredPrefabPath(arguments);
        var gameObjectPath = JsonHelpers.GetString(arguments, "root_path");

        var maxDepth = JsonHelpers.GetInt(arguments, "max_depth") ?? SceneToolLimits.MaxDepthDefault;
        if (maxDepth is < SceneToolLimits.MaxDepthMin or > SceneToolLimits.MaxDepthMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"max_depth must be between {SceneToolLimits.MaxDepthMin} and {SceneToolLimits.MaxDepthMax}",
                new JsonObject { ["max_depth"] = maxDepth });
        }

        var maxGameObjects = JsonHelpers.GetInt(arguments, "max_game_objects") ?? SceneToolLimits.MaxGameObjectsDefault;
        if (maxGameObjects is < SceneToolLimits.MaxGameObjectsMin or > SceneToolLimits.MaxGameObjectsMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"max_game_objects must be between {SceneToolLimits.MaxGameObjectsMin} and {SceneToolLimits.MaxGameObjectsMax}",
                new JsonObject { ["max_game_objects"] = maxGameObjects });
        }

        var offset = JsonHelpers.GetInt(arguments, "offset") ?? 0;
        if (offset < 0)
        {
            throw new McpException(ErrorCodes.InvalidParams, "offset must be >= 0",
                new JsonObject { ["offset"] = offset });
        }

        var componentFilter = ParseComponentFilter(arguments);

        return new GetPrefabHierarchyRequest(prefabPath, gameObjectPath, maxDepth, maxGameObjects, offset, componentFilter);
    }

    private static GetPrefabComponentInfoRequest ParseGetPrefabComponentInfoRequest(JsonObject arguments)
    {
        var prefabPath = ParseRequiredPrefabPath(arguments);

        var gameObjectPath = JsonHelpers.GetString(arguments, "game_object_path");
        if (string.IsNullOrWhiteSpace(gameObjectPath))
        {
            throw new McpException(ErrorCodes.InvalidParams, "game_object_path is required");
        }

        var index = JsonHelpers.GetInt(arguments, "index");
        if (index.HasValue && index.Value < 0)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                "index must be >= 0",
                new JsonObject { ["index"] = index.Value });
        }

        string[]? fields = null;
        if (arguments.TryGetPropertyValue("fields", out var fieldsNode) && fieldsNode is JsonArray fieldsArray)
        {
            fields = fieldsArray
                .Select(n => n?.GetValue<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray()!;
        }

        var maxArrayElements = JsonHelpers.GetInt(arguments, "max_array_elements") ?? SceneToolLimits.MaxArrayElementsDefault;
        if (maxArrayElements is < SceneToolLimits.MaxArrayElementsMin or > SceneToolLimits.MaxArrayElementsMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"max_array_elements must be between {SceneToolLimits.MaxArrayElementsMin} and {SceneToolLimits.MaxArrayElementsMax}",
                new JsonObject { ["max_array_elements"] = maxArrayElements });
        }

        return new GetPrefabComponentInfoRequest(prefabPath, gameObjectPath, index, fields, maxArrayElements);
    }

    private static ManagePrefabComponentRequest ParseManagePrefabComponentRequest(JsonObject arguments)
    {
        var prefabPath = ParseRequiredPrefabPath(arguments);

        var action = JsonHelpers.GetString(arguments, "action");
        if (!ManageActions.IsSupported(action))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"action must be one of {ManageActions.Add}|{ManageActions.Update}|{ManageActions.Remove}|{ManageActions.Move}",
                new JsonObject { ["action"] = action });
        }

        var gameObjectPath = JsonHelpers.GetString(arguments, "game_object_path");
        if (string.IsNullOrWhiteSpace(gameObjectPath))
        {
            throw new McpException(ErrorCodes.InvalidParams, "game_object_path is required");
        }

        var componentType = JsonHelpers.GetString(arguments, "component_type");
        var index = JsonHelpers.GetInt(arguments, "index");
        var newIndex = JsonHelpers.GetInt(arguments, "new_index");
        JsonObject? fields = null;
        if (arguments.TryGetPropertyValue("fields", out var fieldsNode) && fieldsNode is JsonObject fieldsObj)
        {
            fields = fieldsObj;
        }

        switch (action)
        {
            case ManageActions.Add:
                if (string.IsNullOrWhiteSpace(componentType))
                {
                    throw new McpException(ErrorCodes.InvalidParams, "component_type is required for 'add' action");
                }

                break;
            case ManageActions.Update:
                if (!index.HasValue)
                {
                    throw new McpException(ErrorCodes.InvalidParams, "index is required for 'update' action");
                }

                if (fields is null)
                {
                    throw new McpException(ErrorCodes.InvalidParams, "fields is required for 'update' action");
                }

                break;
            case ManageActions.Remove:
                if (!index.HasValue)
                {
                    throw new McpException(ErrorCodes.InvalidParams, "index is required for 'remove' action");
                }

                break;
            case ManageActions.Move:
                if (!index.HasValue)
                {
                    throw new McpException(ErrorCodes.InvalidParams, "index is required for 'move' action");
                }

                if (!newIndex.HasValue)
                {
                    throw new McpException(ErrorCodes.InvalidParams, "new_index is required for 'move' action");
                }

                break;
        }

        return new ManagePrefabComponentRequest(prefabPath, action!, gameObjectPath, componentType, index, newIndex, fields);
    }

    private static ManageSceneGameObjectRequest ParseManageSceneGameObjectRequest(JsonObject arguments)
    {
        var action = JsonHelpers.GetString(arguments, "action");
        if (!GameObjectActions.IsSupported(action))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"action must be one of {GameObjectActions.Create}|{GameObjectActions.Update}|{GameObjectActions.Delete}|{GameObjectActions.Reparent}",
                new JsonObject { ["action"] = action });
        }

        var gameObjectPath = JsonHelpers.GetString(arguments, "game_object_path");
        var parentPath = JsonHelpers.GetString(arguments, "parent_path");
        var name = JsonHelpers.GetString(arguments, "name");
        var tag = JsonHelpers.GetString(arguments, "tag");
        var layer = JsonHelpers.GetInt(arguments, "layer");
        bool? active = null;
        if (arguments.TryGetPropertyValue("active", out var activeNode) && activeNode is not null)
        {
            active = activeNode.GetValue<bool>();
        }

        var primitiveType = JsonHelpers.GetString(arguments, "primitive_type");
        bool? worldPositionStays = null;
        if (arguments.TryGetPropertyValue("world_position_stays", out var wpsNode) && wpsNode is not null)
        {
            worldPositionStays = wpsNode.GetValue<bool>();
        }

        var siblingIndex = JsonHelpers.GetInt(arguments, "sibling_index");

        switch (action)
        {
            case GameObjectActions.Create:
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new McpException(ErrorCodes.InvalidParams, "name is required for 'create' action");
                }

                if (primitiveType is not null && !PrimitiveTypes.IsSupported(primitiveType))
                {
                    throw new McpException(
                        ErrorCodes.InvalidParams,
                        $"primitive_type must be one of Cube|Sphere|Capsule|Cylinder|Plane|Quad",
                        new JsonObject { ["primitive_type"] = primitiveType });
                }

                break;
            case GameObjectActions.Update:
                if (string.IsNullOrWhiteSpace(gameObjectPath))
                {
                    throw new McpException(ErrorCodes.InvalidParams, "game_object_path is required for 'update' action");
                }

                break;
            case GameObjectActions.Delete:
                if (string.IsNullOrWhiteSpace(gameObjectPath))
                {
                    throw new McpException(ErrorCodes.InvalidParams, "game_object_path is required for 'delete' action");
                }

                break;
            case GameObjectActions.Reparent:
                if (string.IsNullOrWhiteSpace(gameObjectPath))
                {
                    throw new McpException(ErrorCodes.InvalidParams, "game_object_path is required for 'reparent' action");
                }

                break;
        }

        if (layer.HasValue && (layer.Value < 0 || layer.Value > 31))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                "layer must be between 0 and 31",
                new JsonObject { ["layer"] = layer.Value });
        }

        return new ManageSceneGameObjectRequest(action!, gameObjectPath, parentPath, name, tag, layer, active, primitiveType, worldPositionStays, siblingIndex);
    }

    private static ManagePrefabGameObjectRequest ParseManagePrefabGameObjectRequest(JsonObject arguments)
    {
        var prefabPath = ParseRequiredPrefabPath(arguments);

        var action = JsonHelpers.GetString(arguments, "action");
        if (!GameObjectActions.IsSupported(action))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"action must be one of {GameObjectActions.Create}|{GameObjectActions.Update}|{GameObjectActions.Delete}|{GameObjectActions.Reparent}",
                new JsonObject { ["action"] = action });
        }

        var gameObjectPath = JsonHelpers.GetString(arguments, "game_object_path");
        var parentPath = JsonHelpers.GetString(arguments, "parent_path");
        var name = JsonHelpers.GetString(arguments, "name");
        var tag = JsonHelpers.GetString(arguments, "tag");
        var layer = JsonHelpers.GetInt(arguments, "layer");
        bool? active = null;
        if (arguments.TryGetPropertyValue("active", out var activeNode) && activeNode is not null)
        {
            active = activeNode.GetValue<bool>();
        }

        var primitiveType = JsonHelpers.GetString(arguments, "primitive_type");
        bool? worldPositionStays = null;
        if (arguments.TryGetPropertyValue("world_position_stays", out var wpsNode) && wpsNode is not null)
        {
            worldPositionStays = wpsNode.GetValue<bool>();
        }

        var siblingIndex = JsonHelpers.GetInt(arguments, "sibling_index");

        switch (action)
        {
            case GameObjectActions.Create:
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new McpException(ErrorCodes.InvalidParams, "name is required for 'create' action");
                }

                if (primitiveType is not null && !PrimitiveTypes.IsSupported(primitiveType))
                {
                    throw new McpException(
                        ErrorCodes.InvalidParams,
                        $"primitive_type must be one of Cube|Sphere|Capsule|Cylinder|Plane|Quad",
                        new JsonObject { ["primitive_type"] = primitiveType });
                }

                break;
            case GameObjectActions.Update:
                if (string.IsNullOrWhiteSpace(gameObjectPath))
                {
                    throw new McpException(ErrorCodes.InvalidParams, "game_object_path is required for 'update' action");
                }

                break;
            case GameObjectActions.Delete:
                if (string.IsNullOrWhiteSpace(gameObjectPath))
                {
                    throw new McpException(ErrorCodes.InvalidParams, "game_object_path is required for 'delete' action");
                }

                break;
            case GameObjectActions.Reparent:
                if (string.IsNullOrWhiteSpace(gameObjectPath))
                {
                    throw new McpException(ErrorCodes.InvalidParams, "game_object_path is required for 'reparent' action");
                }

                break;
        }

        if (layer.HasValue && (layer.Value < 0 || layer.Value > 31))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                "layer must be between 0 and 31",
                new JsonObject { ["layer"] = layer.Value });
        }

        return new ManagePrefabGameObjectRequest(prefabPath, action!, gameObjectPath, parentPath, name, tag, layer, active, primitiveType, worldPositionStays, siblingIndex);
    }

    private static InstantiatePrefabRequest ParseInstantiatePrefabRequest(JsonObject arguments)
    {
        var prefabPath = ParseRequiredPrefabPath(arguments);
        var parentPath = JsonHelpers.GetString(arguments, "parent_path");
        var name = JsonHelpers.GetString(arguments, "name");
        var siblingIndex = JsonHelpers.GetInt(arguments, "sibling_index");

        JsonObject? position = null;
        if (arguments.TryGetPropertyValue("position", out var posNode) && posNode is JsonObject posObj)
        {
            position = posObj;
        }

        JsonObject? rotation = null;
        if (arguments.TryGetPropertyValue("rotation", out var rotNode) && rotNode is JsonObject rotObj)
        {
            rotation = rotObj;
        }

        return new InstantiatePrefabRequest(prefabPath, parentPath, position, rotation, name, siblingIndex);
    }

    private static GetAssetInfoRequest ParseGetAssetInfoRequest(JsonObject arguments)
    {
        var assetPath = JsonHelpers.GetString(arguments, "asset_path");
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            throw new McpException(ErrorCodes.InvalidParams, "asset_path is required");
        }

        return new GetAssetInfoRequest(assetPath);
    }

    private static FindPrefabGameObjectsRequest ParseFindPrefabGameObjectsRequest(JsonObject arguments)
    {
        var prefabPath = ParseRequiredPrefabPath(arguments);
        var name = JsonHelpers.GetString(arguments, "name");
        var tag = JsonHelpers.GetString(arguments, "tag");
        var componentType = JsonHelpers.GetString(arguments, "component_type");
        var rootPath = JsonHelpers.GetString(arguments, "root_path");
        var layer = JsonHelpers.GetInt(arguments, "layer");
        bool? active = null;
        if (arguments.TryGetPropertyValue("active", out var activeNode) && activeNode is not null)
        {
            active = activeNode.GetValue<bool>();
        }

        if (layer.HasValue && (layer.Value < 0 || layer.Value > 31))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                "layer must be between 0 and 31",
                new JsonObject { ["layer"] = layer.Value });
        }

        var maxResults = JsonHelpers.GetInt(arguments, "max_results") ?? FindSceneGameObjectsLimits.MaxResultsDefault;
        if (maxResults is < FindSceneGameObjectsLimits.MaxResultsMin or > FindSceneGameObjectsLimits.MaxResultsMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"max_results must be between {FindSceneGameObjectsLimits.MaxResultsMin} and {FindSceneGameObjectsLimits.MaxResultsMax}",
                new JsonObject { ["max_results"] = maxResults });
        }

        var offset = JsonHelpers.GetInt(arguments, "offset") ?? 0;
        if (offset < 0)
        {
            throw new McpException(ErrorCodes.InvalidParams, "offset must be >= 0",
                new JsonObject { ["offset"] = offset });
        }

        return new FindPrefabGameObjectsRequest(prefabPath, name, tag, componentType, rootPath, layer, active, maxResults, offset);
    }

    private static ManageAssetRequest ParseManageAssetRequest(JsonObject arguments)
    {
        var action = JsonHelpers.GetString(arguments, "action");
        if (!ManageAssetActions.IsSupported(action))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"action must be one of {ManageAssetActions.Create}|{ManageAssetActions.Delete}|{ManageAssetActions.GetProperties}|{ManageAssetActions.SetProperties}|{ManageAssetActions.SetShader}|{ManageAssetActions.GetKeywords}|{ManageAssetActions.SetKeywords}",
                new JsonObject { ["action"] = action });
        }

        var assetPath = JsonHelpers.GetString(arguments, "asset_path");
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            throw new McpException(ErrorCodes.InvalidParams, "asset_path is required");
        }

        var assetType = JsonHelpers.GetString(arguments, "asset_type");
        if (action == ManageAssetActions.Create)
        {
            if (string.IsNullOrWhiteSpace(assetType))
            {
                throw new McpException(ErrorCodes.InvalidParams, "asset_type is required for 'create' action");
            }

            if (!AssetTypes.IsSupported(assetType))
            {
                throw new McpException(
                    ErrorCodes.InvalidParams,
                    $"asset_type must be one of {AssetTypes.Material}|{AssetTypes.Folder}|{AssetTypes.PhysicMaterial}|{AssetTypes.AnimatorController}|{AssetTypes.RenderTexture}",
                    new JsonObject { ["asset_type"] = assetType });
            }
        }

        JsonObject? properties = null;
        if (arguments.TryGetPropertyValue("properties", out var propsNode) && propsNode is JsonObject propsObj)
        {
            properties = propsObj;
        }

        bool overwrite = false;
        if (arguments.TryGetPropertyValue("overwrite", out var overwriteNode) && overwriteNode is not null)
        {
            overwrite = overwriteNode.GetValue<bool>();
        }

        var shaderName = JsonHelpers.GetString(arguments, "shader_name");
        if (action == ManageAssetActions.SetShader && string.IsNullOrWhiteSpace(shaderName))
        {
            throw new McpException(ErrorCodes.InvalidParams, "shader_name is required for 'set_shader' action");
        }

        if (action == ManageAssetActions.SetProperties && (properties is null || properties.Count == 0))
        {
            throw new McpException(ErrorCodes.InvalidParams, "properties is required for 'set_properties' action");
        }

        string[]? keywords = null;
        if (arguments.TryGetPropertyValue("keywords", out var kwNode) && kwNode is JsonArray kwArray)
        {
            keywords = kwArray
                .Select(n => n?.GetValue<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray()!;
        }

        string? keywordsAction = JsonHelpers.GetString(arguments, "keywords_action");
        if (action == ManageAssetActions.SetKeywords)
        {
            if (keywords is null || keywords.Length == 0)
            {
                throw new McpException(ErrorCodes.InvalidParams, "keywords is required for 'set_keywords' action");
            }

            if (!KeywordsActions.IsSupported(keywordsAction))
            {
                throw new McpException(
                    ErrorCodes.InvalidParams,
                    $"keywords_action must be one of {KeywordsActions.Enable}|{KeywordsActions.Disable}",
                    new JsonObject { ["keywords_action"] = keywordsAction });
            }
        }

        return new ManageAssetRequest(action!, assetPath, assetType, properties, overwrite, shaderName, keywords, keywordsAction);
    }

    private static CaptureScreenshotRequest ParseCaptureScreenshotRequest(JsonObject arguments)
    {
        var source = JsonHelpers.GetString(arguments, "source") ?? ScreenshotSources.GameView;
        if (!ScreenshotSources.IsSupported(source))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"source must be one of {ScreenshotSources.GameView}|{ScreenshotSources.SceneView}|{ScreenshotSources.Camera}",
                new JsonObject { ["source"] = source });
        }

        var width = JsonHelpers.GetInt(arguments, "width") ?? ScreenshotLimits.WidthDefault;
        if (width is < ScreenshotLimits.WidthMin or > ScreenshotLimits.WidthMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"width must be between {ScreenshotLimits.WidthMin} and {ScreenshotLimits.WidthMax}",
                new JsonObject { ["width"] = width });
        }

        var height = JsonHelpers.GetInt(arguments, "height") ?? ScreenshotLimits.HeightDefault;
        if (height is < ScreenshotLimits.HeightMin or > ScreenshotLimits.HeightMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"height must be between {ScreenshotLimits.HeightMin} and {ScreenshotLimits.HeightMax}",
                new JsonObject { ["height"] = height });
        }

        var cameraPath = JsonHelpers.GetString(arguments, "camera_path");
        var outputPath = JsonHelpers.GetString(arguments, "output_path");

        return new CaptureScreenshotRequest(source, width, height, cameraPath, outputPath);
    }

    private static ManageAsmdefRequest ParseManageAsmdefRequest(JsonObject arguments)
    {
        var action = JsonHelpers.GetString(arguments, "action");
        if (!ManageAsmdefActions.IsSupported(action))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"action must be one of {ManageAsmdefActions.List}|{ManageAsmdefActions.Get}|{ManageAsmdefActions.Create}|{ManageAsmdefActions.Update}|{ManageAsmdefActions.Delete}|{ManageAsmdefActions.AddReference}|{ManageAsmdefActions.RemoveReference}",
                new JsonObject { ["action"] = action });
        }

        var name = JsonHelpers.GetString(arguments, "name");
        var guid = JsonHelpers.GetString(arguments, "guid");

        // name / guid 排他チェック
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(guid))
        {
            throw new McpException(ErrorCodes.InvalidParams, "Specify either 'name' or 'guid', not both");
        }

        // action ごとの必須チェック
        if (action is ManageAsmdefActions.Get or ManageAsmdefActions.Update or ManageAsmdefActions.Delete
            or ManageAsmdefActions.AddReference or ManageAsmdefActions.RemoveReference)
        {
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(guid))
            {
                throw new McpException(ErrorCodes.InvalidParams, $"'name' or 'guid' is required for '{action}' action");
            }
        }

        if (action == ManageAsmdefActions.Create)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new McpException(ErrorCodes.InvalidParams, "'name' is required for 'create' action");
            }

            var directory = JsonHelpers.GetString(arguments, "directory");
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new McpException(ErrorCodes.InvalidParams, "'directory' is required for 'create' action");
            }
        }

        var reference = JsonHelpers.GetString(arguments, "reference");
        var referenceGuid = JsonHelpers.GetString(arguments, "reference_guid");

        // reference / reference_guid 排他チェック
        if (!string.IsNullOrWhiteSpace(reference) && !string.IsNullOrWhiteSpace(referenceGuid))
        {
            throw new McpException(ErrorCodes.InvalidParams, "Specify either 'reference' or 'reference_guid', not both");
        }

        if (action is ManageAsmdefActions.AddReference or ManageAsmdefActions.RemoveReference)
        {
            if (string.IsNullOrWhiteSpace(reference) && string.IsNullOrWhiteSpace(referenceGuid))
            {
                throw new McpException(ErrorCodes.InvalidParams, $"'reference' or 'reference_guid' is required for '{action}' action");
            }
        }

        var directory2 = JsonHelpers.GetString(arguments, "directory");
        var rootNamespace = JsonHelpers.GetString(arguments, "root_namespace");
        var useGuids = JsonHelpers.GetBool(arguments, "use_guids");

        string[]? references = null;
        if (arguments.TryGetPropertyValue("references", out var refsNode) && refsNode is JsonArray refsArray)
        {
            references = refsArray
                .Select(n => n?.GetValue<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray()!;
        }

        string[]? includePlatforms = null;
        if (arguments.TryGetPropertyValue("include_platforms", out var ipNode) && ipNode is JsonArray ipArray)
        {
            includePlatforms = ipArray.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()!;
        }

        string[]? excludePlatforms = null;
        if (arguments.TryGetPropertyValue("exclude_platforms", out var epNode) && epNode is JsonArray epArray)
        {
            excludePlatforms = epArray.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()!;
        }

        string[]? defineConstraints = null;
        if (arguments.TryGetPropertyValue("define_constraints", out var dcNode) && dcNode is JsonArray dcArray)
        {
            defineConstraints = dcArray.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()!;
        }

        var allowUnsafeCode = JsonHelpers.GetBool(arguments, "allow_unsafe_code");
        var autoReferenced = JsonHelpers.GetBool(arguments, "auto_referenced");
        var noEngineReferences = JsonHelpers.GetBool(arguments, "no_engine_references");

        var namePattern = JsonHelpers.GetString(arguments, "name_pattern");
        var maxResults = JsonHelpers.GetInt(arguments, "max_results") ?? ManageAsmdefLimits.MaxResultsDefault;
        if (maxResults is < ManageAsmdefLimits.MaxResultsMin or > ManageAsmdefLimits.MaxResultsMax)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"max_results must be between {ManageAsmdefLimits.MaxResultsMin} and {ManageAsmdefLimits.MaxResultsMax}",
                new JsonObject { ["max_results"] = maxResults });
        }

        var offset = JsonHelpers.GetInt(arguments, "offset") ?? 0;
        if (offset < 0)
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                "offset must be >= 0",
                new JsonObject { ["offset"] = offset });
        }

        return new ManageAsmdefRequest(
            action!, name, guid, directory2, rootNamespace,
            references, useGuids, includePlatforms, excludePlatforms,
            allowUnsafeCode, autoReferenced, defineConstraints, noEngineReferences,
            reference, referenceGuid, namePattern, maxResults, offset);
    }

    private static ManagePrefabRequest ParseManagePrefabRequest(JsonObject arguments)
    {
        var action = JsonHelpers.GetString(arguments, "action");
        if (!ManagePrefabActions.IsSupported(action))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"action must be one of {ManagePrefabActions.Save}|{ManagePrefabActions.Apply}|{ManagePrefabActions.Unpack}|{ManagePrefabActions.GetStatus}",
                new JsonObject { ["action"] = action });
        }

        var gameObjectPath = JsonHelpers.GetString(arguments, "game_object_path");
        string? prefabPath = null;
        bool? connect = null;
        bool? completely = null;

        switch (action)
        {
            case ManagePrefabActions.Save:
                prefabPath = JsonHelpers.GetString(arguments, "prefab_path");
                if (string.IsNullOrWhiteSpace(prefabPath))
                {
                    throw new McpException(ErrorCodes.InvalidParams, "prefab_path is required for 'save' action");
                }
                connect = JsonHelpers.GetBool(arguments, "connect");
                break;

            case ManagePrefabActions.Apply:
            case ManagePrefabActions.Unpack:
            case ManagePrefabActions.GetStatus:
                if (string.IsNullOrWhiteSpace(gameObjectPath))
                {
                    throw new McpException(ErrorCodes.InvalidParams, $"game_object_path is required for '{action}' action");
                }
                if (action == ManagePrefabActions.Unpack)
                {
                    completely = JsonHelpers.GetBool(arguments, "completely");
                }
                break;
        }

        return new ManagePrefabRequest(action!, gameObjectPath, prefabPath, connect, completely);
    }
}

internal static class ToolResultFormatter
{
    public static JsonObject Success(object payload)
    {
        var structuredContent = ToStructuredContent(payload);
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = structuredContent.ToJsonString(JsonDefaults.Options),
                },
            },
            ["structuredContent"] = structuredContent.DeepClone(),
        };
    }

    public static JsonObject Error(McpException error)
    {
        var semantics = ErrorSemanticsResolver.Resolve(error.Code);
        var details = ErrorSemanticsResolver.EnsureFailureDetails(
            JsonHelpers.CloneNode(error.Details),
            semantics.ExecutionGuarantee,
            semantics.RecoveryAction);

        var payload = new JsonObject
        {
            ["code"] = error.Code,
            ["message"] = error.Message,
            ["retryable"] = semantics.Retryable,
            ["details"] = details,
        };

        return new JsonObject
        {
            ["isError"] = true,
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload.ToJsonString(JsonDefaults.Options),
                },
            },
            ["structuredContent"] = payload,
        };
    }

    internal static JsonNode ToStructuredContent(object payload)
    {
        return payload switch
        {
            JsonNode node => node.DeepClone(),
            _ => JsonSerializer.SerializeToNode(payload, JsonDefaults.Options) ?? new JsonObject(),
        };
    }

    public static JsonObject SuccessWithImage(JsonNode structuredContent, string base64Data, string mimeType)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = base64Data,
                    ["mimeType"] = mimeType,
                },
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = structuredContent.ToJsonString(JsonDefaults.Options),
                },
            },
        };
    }
}

internal sealed class McpHttpHandler
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);

    private sealed record ProcessResult(JsonObject? Response, string? NewSessionId)
    {
        public static ProcessResult NoContent() => new(null, null);
    }

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly McpToolService _toolService;

    public McpHttpHandler(McpToolService toolService)
    {
        _toolService = toolService;
    }

    public async Task HandlePostAsync(HttpContext context)
    {
        PruneExpiredSessions();

        JsonNode? requestPayload;
        try
        {
            requestPayload = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            await JsonRpc.WriteAsync(context, JsonRpc.Error(null, -32700, "Parse error"));
            return;
        }

        if (requestPayload is null)
        {
            await JsonRpc.WriteAsync(context, JsonRpc.Error(null, -32600, "Invalid Request"));
            return;
        }

        if (requestPayload is JsonObject singleRequest)
        {
            var result = await ProcessRequestAsync(context, singleRequest);
            if (result.NewSessionId is not null)
            {
                context.Response.Headers[Constants.McpSessionHeader] = result.NewSessionId;
            }

            if (result.Response is null)
            {
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            await JsonRpc.WriteAsync(context, result.Response);
            return;
        }

        if (requestPayload is JsonArray batch)
        {
            var responses = new JsonArray();
            string? newSessionId = null;

            foreach (var requestNode in batch)
            {
                if (requestNode is not JsonObject requestObject)
                {
                    responses.Add(JsonRpc.Error(null, -32600, "Invalid Request"));
                    continue;
                }

                var result = await ProcessRequestAsync(context, requestObject);
                newSessionId ??= result.NewSessionId;
                if (result.Response is not null)
                {
                    responses.Add(result.Response);
                }
            }

            if (newSessionId is not null)
            {
                context.Response.Headers[Constants.McpSessionHeader] = newSessionId;
            }

            if (responses.Count == 0)
            {
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            await JsonRpc.WriteAsync(context, responses);
            return;
        }

        await JsonRpc.WriteAsync(context, JsonRpc.Error(null, -32600, "Invalid Request"));
    }

    public async Task HandleGetAsync(HttpContext context)
    {
        PruneExpiredSessions();

        var sessionId = GetSessionId(context.Request);
        if (string.IsNullOrWhiteSpace(sessionId) || !TryTouchSession(sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await JsonRpc.WriteAsync(context, JsonRpc.Error(null, -32000, "Bad Request: No valid session ID provided"));
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentType = "text/event-stream";

        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                if (!TryTouchSession(sessionId))
                {
                    return;
                }

                await context.Response.WriteAsync(": keep-alive\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
    }

    public async Task HandleDeleteAsync(HttpContext context)
    {
        PruneExpiredSessions();

        var sessionId = GetSessionId(context.Request);
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryRemove(sessionId, out _))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await JsonRpc.WriteAsync(context, JsonRpc.Error(null, -32000, "Bad Request: No valid session ID provided"));
            return;
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task<ProcessResult> ProcessRequestAsync(HttpContext context, JsonObject request)
    {
        PruneExpiredSessions();

        var id = JsonHelpers.CloneNode(request["id"]);
        var method = JsonHelpers.GetString(request, "method");
        if (string.IsNullOrWhiteSpace(method))
        {
            return new ProcessResult(JsonRpc.Error(id, -32600, "Invalid Request: method is required"), null);
        }

        var sessionId = GetSessionId(context.Request);
        if (!string.IsNullOrWhiteSpace(sessionId) && !TryTouchSession(sessionId))
        {
            return new ProcessResult(JsonRpc.Error(id, -32000, "Bad Request: No valid session ID provided"), null);
        }

        return method switch
        {
            "initialize" => HandleInitialize(id, request, sessionId),
            "ping" => new ProcessResult(JsonRpc.Result(id, new JsonObject()), null),
            "tools/list" => HandleToolsList(id),
            "tools/call" => await HandleToolCallAsync(context, id, request),
            _ when method.StartsWith("notifications/", StringComparison.Ordinal) => ProcessResult.NoContent(),
            _ => new ProcessResult(JsonRpc.Error(id, -32601, $"Method not found: {method}"), null),
        };
    }

    private ProcessResult HandleInitialize(JsonNode? id, JsonObject request, string? sessionId)
    {
        var activeSessionId = sessionId;
        if (string.IsNullOrWhiteSpace(activeSessionId))
        {
            activeSessionId = Guid.NewGuid().ToString("D");
            _sessions[activeSessionId] = DateTimeOffset.UtcNow;
        }

        var parameters = JsonHelpers.AsObjectOrEmpty(request["params"]);
        var protocolVersion = JsonHelpers.GetString(parameters, "protocolVersion") ?? Constants.DefaultMcpProtocolVersion;

        var result = new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject
                {
                    ["listChanged"] = false,
                },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = Constants.ServerName,
                ["version"] = Constants.ServerVersion,
            },
        };

        return new ProcessResult(JsonRpc.Result(id, result), activeSessionId);
    }

    private static ProcessResult HandleToolsList(JsonNode? id)
    {
        var result = new JsonObject
        {
            ["tools"] = ToolCatalog.BuildMcpTools(),
        };

        return new ProcessResult(JsonRpc.Result(id, result), null);
    }

    private async Task<ProcessResult> HandleToolCallAsync(HttpContext context, JsonNode? id, JsonObject request)
    {
        var parameters = JsonHelpers.AsObjectOrEmpty(request["params"]);
        var toolName = JsonHelpers.GetString(parameters, "name");
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return new ProcessResult(JsonRpc.Error(id, -32602, "Invalid params: name is required"), null);
        }

        var arguments = JsonHelpers.AsObjectOrEmpty(parameters["arguments"]);
        var toolResult = await _toolService.CallToolAsync(toolName, arguments, context.RequestAborted);
        return new ProcessResult(JsonRpc.Result(id, toolResult), null);
    }

    private static string? GetSessionId(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(Constants.McpSessionHeader, out var values))
        {
            return null;
        }

        var sessionId = values.ToString();
        return string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
    }

    private bool TryTouchSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var lastSeen))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - lastSeen > SessionTtl)
        {
            _sessions.TryRemove(sessionId, out _);
            return false;
        }

        _sessions[sessionId] = DateTimeOffset.UtcNow;
        return true;
    }

    private void PruneExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (sessionId, lastSeen) in _sessions)
        {
            if (now - lastSeen > SessionTtl)
            {
                _sessions.TryRemove(sessionId, out _);
            }
        }
    }
}
