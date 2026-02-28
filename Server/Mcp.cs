using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace UnityMcpServer;

internal sealed class McpToolService
{
    private readonly RuntimeState _runtimeState;
    private readonly UnityBridge _unityBridge;

    public McpToolService(RuntimeState runtimeState, UnityBridge unityBridge)
    {
        _runtimeState = runtimeState;
        _unityBridge = unityBridge;
    }

    public async Task<JsonObject> CallToolAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await ExecuteToolAsync(toolName, arguments, cancellationToken);
            return ToolResultFormatter.Success(payload);
        }
        catch (McpException ex)
        {
            return ToolResultFormatter.Error(ex);
        }
        catch (Exception ex)
        {
            return ToolResultFormatter.Error(new McpException(
                ErrorCodes.UnityExecution,
                "Unexpected server error",
                new JsonObject { ["message"] = ex.Message }));
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
            ToolNames.RunTests => await _unityBridge.RunTestsAsync(ParseRunTestsRequest(arguments), cancellationToken),
            ToolNames.GetJobStatus => await _unityBridge.GetJobStatusAsync(ParseJobStatusRequest(arguments), cancellationToken),
            ToolNames.CancelJob => await _unityBridge.CancelJobAsync(ParseCancelJobRequest(arguments), cancellationToken),
            ToolNames.GetSceneHierarchy => (await _unityBridge.GetSceneHierarchyAsync(ParseGetSceneHierarchyRequest(arguments), cancellationToken)).Payload,
            ToolNames.GetSceneComponentInfo => (await _unityBridge.GetSceneComponentInfoAsync(ParseGetSceneComponentInfoRequest(arguments), cancellationToken)).Payload,
            ToolNames.ManageSceneComponent => (await _unityBridge.ManageSceneComponentAsync(ParseManageSceneComponentRequest(arguments), cancellationToken)).Payload,
            ToolNames.GetPrefabHierarchy => (await _unityBridge.GetPrefabHierarchyAsync(ParseGetPrefabHierarchyRequest(arguments), cancellationToken)).Payload,
            ToolNames.GetPrefabComponentInfo => (await _unityBridge.GetPrefabComponentInfoAsync(ParseGetPrefabComponentInfoRequest(arguments), cancellationToken)).Payload,
            ToolNames.ManagePrefabComponent => (await _unityBridge.ManagePrefabComponentAsync(ParseManagePrefabComponentRequest(arguments), cancellationToken)).Payload,
            ToolNames.ManageSceneGameObject => (await _unityBridge.ManageSceneGameObjectAsync(ParseManageSceneGameObjectRequest(arguments), cancellationToken)).Payload,
            ToolNames.ManagePrefabGameObject => (await _unityBridge.ManagePrefabGameObjectAsync(ParseManagePrefabGameObjectRequest(arguments), cancellationToken)).Payload,
            _ => throw new McpException(ErrorCodes.UnknownCommand, $"Unknown tool: {toolName}"),
        };
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

        return new ReadConsoleRequest(maxEntries);
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

        string? filter = null;
        if (!arguments.TryGetPropertyValue("filter", out var node) || node is null)
        {
            return new RunTestsRequest(mode, null);
        }

        filter = JsonHelpers.GetString(arguments, "filter");
        if (string.IsNullOrWhiteSpace(filter))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                "filter must be a non-empty string when provided");
        }

        return new RunTestsRequest(mode, filter);
    }

    private static ControlPlayModeRequest ParseControlPlayModeRequest(JsonObject arguments)
    {
        var action = JsonHelpers.GetString(arguments, "action");
        if (!PlayModeActions.IsSupported(action))
        {
            throw new McpException(
                ErrorCodes.InvalidParams,
                $"action must be one of {PlayModeActions.Start}|{PlayModeActions.Stop}|{PlayModeActions.Pause}",
                new JsonObject { ["action"] = action });
        }

        return new ControlPlayModeRequest(action!);
    }

    private static JobStatusRequest ParseJobStatusRequest(JsonObject arguments)
    {
        return new JobStatusRequest(ParseRequiredJobId(arguments));
    }

    private static CancelJobRequest ParseCancelJobRequest(JsonObject arguments)
    {
        return new CancelJobRequest(ParseRequiredJobId(arguments));
    }

    private static string ParseRequiredJobId(JsonObject arguments)
    {
        var jobId = JsonHelpers.GetString(arguments, "job_id");
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new McpException(ErrorCodes.InvalidParams, "job_id is required");
        }

        return jobId;
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

        return new GetSceneHierarchyRequest(rootPath, maxDepth, maxGameObjects);
    }

    private static GetSceneComponentInfoRequest ParseGetSceneComponentInfoRequest(JsonObject arguments)
    {
        var gameObjectPath = JsonHelpers.GetString(arguments, "game_object_path");
        if (string.IsNullOrWhiteSpace(gameObjectPath))
        {
            throw new McpException(ErrorCodes.InvalidParams, "game_object_path is required");
        }

        var index = JsonHelpers.GetInt(arguments, "index");
        if (!index.HasValue)
        {
            throw new McpException(ErrorCodes.InvalidParams, "index is required");
        }

        if (index.Value < 0)
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

        return new GetSceneComponentInfoRequest(gameObjectPath, index.Value, fields, maxArrayElements);
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

    private static string ParseRequiredPrefabPath(JsonObject arguments)
    {
        var prefabPath = JsonHelpers.GetString(arguments, "prefab_path");
        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            throw new McpException(ErrorCodes.InvalidParams, "prefab_path is required");
        }

        return prefabPath;
    }

    private static GetPrefabHierarchyRequest ParseGetPrefabHierarchyRequest(JsonObject arguments)
    {
        var prefabPath = ParseRequiredPrefabPath(arguments);
        var gameObjectPath = JsonHelpers.GetString(arguments, "game_object_path");

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

        return new GetPrefabHierarchyRequest(prefabPath, gameObjectPath, maxDepth, maxGameObjects);
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
        if (!index.HasValue)
        {
            throw new McpException(ErrorCodes.InvalidParams, "index is required");
        }

        if (index.Value < 0)
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

        return new GetPrefabComponentInfoRequest(prefabPath, gameObjectPath, index.Value, fields, maxArrayElements);
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

    private static JsonNode ToStructuredContent(object payload)
    {
        return payload switch
        {
            JsonNode node => node.DeepClone(),
            _ => JsonSerializer.SerializeToNode(payload, JsonDefaults.Options) ?? new JsonObject(),
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
