using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace UnityMcpServer;

internal readonly record struct EditorReadyWaitPolicy(TimeSpan Timeout, string TimeoutErrorCode, string TimeoutErrorMessage);

internal sealed class UnityBridge
{
    private static readonly TimeSpan JobRetention = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ActiveSessionRejectLogThrottle = TimeSpan.FromSeconds(30);
    private const int MaxRetainedJobs = 512;

    private sealed class PendingRequest : IDisposable
    {
        public PendingRequest(string expectedType, TaskCompletionSource<JsonObject> completion, CancellationTokenSource timeoutCts)
        {
            ExpectedType = expectedType;
            Completion = completion;
            TimeoutCts = timeoutCts;
        }

        public string ExpectedType { get; }

        public TaskCompletionSource<JsonObject> Completion { get; }

        public CancellationTokenSource TimeoutCts { get; }

        public void Dispose()
        {
            TimeoutCts.Dispose();
        }
    }

    private sealed record JobRecord(JobState State, JsonNode? Result, DateTimeOffset UpdatedAt);

    private readonly RuntimeState _runtimeState;
    private readonly RequestScheduler _scheduler;
    private readonly UnitySessionRegistry _sessionRegistry = new();
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new(StringComparer.Ordinal);
    private readonly object _activeSessionRejectLogGate = new();

    private CancellationTokenSource? _staleTimerCts;
    private long _lastMessageReceivedAtUtcTicks;
    private int _shutdownRequested;
    private DateTimeOffset _lastActiveSessionRejectLogUtc;
    private int _suppressedActiveSessionRejectLogs;

    public UnityBridge(RuntimeState runtimeState, RequestScheduler scheduler)
    {
        _runtimeState = runtimeState;
        _scheduler = scheduler;
    }

    public async Task HandleWebSocketEndpointAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket request is required.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        try
        {
            await ReceiveLoopAsync(socket, context.RequestAborted);
        }
        catch (McpException ex) when (ex.Code == ErrorCodes.UnityDisconnected)
        {
            // reconnect race path: socket can close while processing hello/capability
        }
        catch (OperationCanceledException) when (IsShuttingDown())
        {
            // shutdown path
        }
        catch (Exception ex)
        {
            if (!IsShuttingDown())
            {
                Logger.Warn("Unity websocket session failed", ("error", ex.Message));
            }
        }
        finally
        {
            OnSocketClosed(socket);
        }
    }

    public void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            return;
        }

        StopStaleTimer();
        FailPendingRequestsAsDisconnected();
        _runtimeState.OnDisconnected();

        var sockets = _sessionRegistry.DrainAll();
        foreach (var socket in sockets)
        {
            try
            {
                socket.Abort();
            }
            catch
            {
                // no-op
            }

            try
            {
                socket.Dispose();
            }
            catch
            {
                // no-op
            }
        }
    }

    public Task<ReadConsoleResult> ReadConsoleAsync(ReadConsoleRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ReadConsole);
            var payload = await ExecuteSyncToolAsync(
                ToolNames.ReadConsole,
                new JsonObject
                {
                    ["max_entries"] = request.MaxEntries,
                },
                timeoutMs,
                token);
            return new ReadConsoleResult(payload);
        }, cancellationToken);
    }

    public Task<GetPlayModeStateResult> GetPlayModeStateAsync(CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetPlayModeState);
            var payload = await ExecuteSyncToolAsync(ToolNames.GetPlayModeState, new JsonObject(), timeoutMs, token);
            return new GetPlayModeStateResult(payload);
        }, cancellationToken);
    }

    public Task<ClearConsoleResult> ClearConsoleAsync(CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ClearConsole);
            var payload = await ExecuteSyncToolAsync(ToolNames.ClearConsole, new JsonObject(), timeoutMs, token);
            return new ClearConsoleResult(payload);
        }, cancellationToken);
    }

    public Task<RefreshAssetsResult> RefreshAssetsAsync(CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.RefreshAssets);
            var payload = await ExecuteSyncToolAsync(ToolNames.RefreshAssets, new JsonObject(), timeoutMs, token);
            return new RefreshAssetsResult(payload);
        }, cancellationToken);
    }

    public Task<ControlPlayModeResult> ControlPlayModeAsync(ControlPlayModeRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ControlPlayMode);
            var payload = await ExecuteSyncToolAsync(
                ToolNames.ControlPlayMode,
                new JsonObject
                {
                    ["action"] = request.Action,
                },
                timeoutMs,
                token);
            return new ControlPlayModeResult(payload);
        }, cancellationToken);
    }

    private async Task<JsonNode> ExecuteSyncToolAsync(
        string toolName,
        JsonObject parameters,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var requestMessage = new JsonObject
        {
            ["type"] = "execute",
            ["protocol_version"] = Constants.ProtocolVersion,
            ["request_id"] = Guid.NewGuid().ToString("D"),
            ["tool_name"] = toolName,
            ["params"] = parameters,
            ["timeout_ms"] = timeoutMs,
        };

        var response = await SendRequestAsync(
            requestMessage,
            "result",
            TimeSpan.FromMilliseconds(timeoutMs),
            cancellationToken);

        var status = JsonHelpers.GetString(response, "status");
        if (string.Equals(status, "ok", StringComparison.Ordinal))
        {
            return JsonHelpers.CloneNode(response["result"]) ?? new JsonObject();
        }

        var errorDetails = JsonHelpers.AsObjectOrEmpty(JsonHelpers.CloneNode(response));
        var pluginErrorCode = JsonHelpers.GetString(errorDetails, "error_code");
        var pluginMessage = JsonHelpers.GetString(errorDetails, "message");
        var wrappedDetails = new JsonObject();
        if (!string.IsNullOrWhiteSpace(pluginErrorCode))
        {
            wrappedDetails["plugin_error_code"] = pluginErrorCode;
        }

        if (!string.IsNullOrWhiteSpace(pluginMessage))
        {
            wrappedDetails["message"] = pluginMessage;
        }

        throw new McpException(
            ErrorCodes.UnityExecution,
            pluginMessage ?? "Unity returned execution error",
            wrappedDetails);
    }

    public Task<GetSceneHierarchyResult> GetSceneHierarchyAsync(GetSceneHierarchyRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetSceneHierarchy);
            var parameters = new JsonObject
            {
                ["max_depth"] = request.MaxDepth,
                ["max_game_objects"] = request.MaxGameObjects,
            };
            if (!string.IsNullOrWhiteSpace(request.RootPath))
            {
                parameters["root_path"] = request.RootPath;
            }

            var payload = await ExecuteSyncToolAsync(ToolNames.GetSceneHierarchy, parameters, timeoutMs, token);
            return new GetSceneHierarchyResult(payload);
        }, cancellationToken);
    }

    public Task<GetSceneComponentInfoResult> GetSceneComponentInfoAsync(GetSceneComponentInfoRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetSceneComponentInfo);
            var parameters = new JsonObject
            {
                ["game_object_path"] = request.GameObjectPath,
                ["index"] = request.Index,
                ["max_array_elements"] = request.MaxArrayElements,
            };
            if (request.Fields is not null)
            {
                var fieldsArray = new JsonArray();
                foreach (var f in request.Fields)
                {
                    fieldsArray.Add(f);
                }

                parameters["fields"] = fieldsArray;
            }

            var payload = await ExecuteSyncToolAsync(ToolNames.GetSceneComponentInfo, parameters, timeoutMs, token);
            return new GetSceneComponentInfoResult(payload);
        }, cancellationToken);
    }

    public Task<ManageSceneComponentResult> ManageSceneComponentAsync(ManageSceneComponentRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ManageSceneComponent);
            var parameters = new JsonObject
            {
                ["action"] = request.Action,
                ["game_object_path"] = request.GameObjectPath,
            };
            if (!string.IsNullOrWhiteSpace(request.ComponentType))
            {
                parameters["component_type"] = request.ComponentType;
            }

            if (request.Index.HasValue)
            {
                parameters["index"] = request.Index.Value;
            }

            if (request.NewIndex.HasValue)
            {
                parameters["new_index"] = request.NewIndex.Value;
            }

            if (request.Fields is not null)
            {
                parameters["fields"] = request.Fields.DeepClone();
            }

            var payload = await ExecuteSyncToolAsync(ToolNames.ManageSceneComponent, parameters, timeoutMs, token);
            return new ManageSceneComponentResult(payload);
        }, cancellationToken);
    }

    public Task<GetPrefabHierarchyResult> GetPrefabHierarchyAsync(GetPrefabHierarchyRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetPrefabHierarchy);
            var parameters = new JsonObject
            {
                ["prefab_path"] = request.PrefabPath,
                ["max_depth"] = request.MaxDepth,
                ["max_game_objects"] = request.MaxGameObjects,
            };
            if (!string.IsNullOrWhiteSpace(request.GameObjectPath))
            {
                parameters["game_object_path"] = request.GameObjectPath;
            }

            var payload = await ExecuteSyncToolAsync(ToolNames.GetPrefabHierarchy, parameters, timeoutMs, token);
            return new GetPrefabHierarchyResult(payload);
        }, cancellationToken);
    }

    public Task<GetPrefabComponentInfoResult> GetPrefabComponentInfoAsync(GetPrefabComponentInfoRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetPrefabComponentInfo);
            var parameters = new JsonObject
            {
                ["prefab_path"] = request.PrefabPath,
                ["game_object_path"] = request.GameObjectPath,
                ["index"] = request.Index,
                ["max_array_elements"] = request.MaxArrayElements,
            };
            if (request.Fields is not null)
            {
                var fieldsArray = new JsonArray();
                foreach (var f in request.Fields)
                {
                    fieldsArray.Add(f);
                }

                parameters["fields"] = fieldsArray;
            }

            var payload = await ExecuteSyncToolAsync(ToolNames.GetPrefabComponentInfo, parameters, timeoutMs, token);
            return new GetPrefabComponentInfoResult(payload);
        }, cancellationToken);
    }

    public Task<ManagePrefabComponentResult> ManagePrefabComponentAsync(ManagePrefabComponentRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ManagePrefabComponent);
            var parameters = new JsonObject
            {
                ["prefab_path"] = request.PrefabPath,
                ["action"] = request.Action,
                ["game_object_path"] = request.GameObjectPath,
            };
            if (!string.IsNullOrWhiteSpace(request.ComponentType))
            {
                parameters["component_type"] = request.ComponentType;
            }

            if (request.Index.HasValue)
            {
                parameters["index"] = request.Index.Value;
            }

            if (request.NewIndex.HasValue)
            {
                parameters["new_index"] = request.NewIndex.Value;
            }

            if (request.Fields is not null)
            {
                parameters["fields"] = request.Fields.DeepClone();
            }

            var payload = await ExecuteSyncToolAsync(ToolNames.ManagePrefabComponent, parameters, timeoutMs, token);
            return new ManagePrefabComponentResult(payload);
        }, cancellationToken);
    }

    public Task<ManageSceneGameObjectResult> ManageSceneGameObjectAsync(ManageSceneGameObjectRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ManageSceneGameObject);
            var parameters = new JsonObject
            {
                ["action"] = request.Action,
            };
            if (!string.IsNullOrWhiteSpace(request.GameObjectPath))
            {
                parameters["game_object_path"] = request.GameObjectPath;
            }

            if (!string.IsNullOrWhiteSpace(request.ParentPath))
            {
                parameters["parent_path"] = request.ParentPath;
            }

            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                parameters["name"] = request.Name;
            }

            if (!string.IsNullOrWhiteSpace(request.Tag))
            {
                parameters["tag"] = request.Tag;
            }

            if (request.Layer.HasValue)
            {
                parameters["layer"] = request.Layer.Value;
            }

            if (request.Active.HasValue)
            {
                parameters["active"] = request.Active.Value;
            }

            if (!string.IsNullOrWhiteSpace(request.PrimitiveType))
            {
                parameters["primitive_type"] = request.PrimitiveType;
            }

            if (request.WorldPositionStays.HasValue)
            {
                parameters["world_position_stays"] = request.WorldPositionStays.Value;
            }

            if (request.SiblingIndex.HasValue)
            {
                parameters["sibling_index"] = request.SiblingIndex.Value;
            }

            var payload = await ExecuteSyncToolAsync(ToolNames.ManageSceneGameObject, parameters, timeoutMs, token);
            return new ManageSceneGameObjectResult(payload);
        }, cancellationToken);
    }

    public Task<ManagePrefabGameObjectResult> ManagePrefabGameObjectAsync(ManagePrefabGameObjectRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ManagePrefabGameObject);
            var parameters = new JsonObject
            {
                ["prefab_path"] = request.PrefabPath,
                ["action"] = request.Action,
            };
            if (!string.IsNullOrWhiteSpace(request.GameObjectPath))
            {
                parameters["game_object_path"] = request.GameObjectPath;
            }

            if (!string.IsNullOrWhiteSpace(request.ParentPath))
            {
                parameters["parent_path"] = request.ParentPath;
            }

            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                parameters["name"] = request.Name;
            }

            if (!string.IsNullOrWhiteSpace(request.Tag))
            {
                parameters["tag"] = request.Tag;
            }

            if (request.Layer.HasValue)
            {
                parameters["layer"] = request.Layer.Value;
            }

            if (request.Active.HasValue)
            {
                parameters["active"] = request.Active.Value;
            }

            if (!string.IsNullOrWhiteSpace(request.PrimitiveType))
            {
                parameters["primitive_type"] = request.PrimitiveType;
            }

            if (request.WorldPositionStays.HasValue)
            {
                parameters["world_position_stays"] = request.WorldPositionStays.Value;
            }

            if (request.SiblingIndex.HasValue)
            {
                parameters["sibling_index"] = request.SiblingIndex.Value;
            }

            var payload = await ExecuteSyncToolAsync(ToolNames.ManagePrefabGameObject, parameters, timeoutMs, token);
            return new ManagePrefabGameObjectResult(payload);
        }, cancellationToken);
    }

    public Task<RunTestsResult> RunTestsAsync(RunTestsRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.RunTests);

            var parameters = new JsonObject
            {
                ["mode"] = request.Mode,
            };
            if (!string.IsNullOrWhiteSpace(request.Filter))
            {
                parameters["filter"] = request.Filter;
            }

            var requestMessage = new JsonObject
            {
                ["type"] = "submit_job",
                ["protocol_version"] = Constants.ProtocolVersion,
                ["request_id"] = Guid.NewGuid().ToString("D"),
                ["tool_name"] = ToolNames.RunTests,
                ["params"] = parameters,
                ["timeout_ms"] = timeoutMs,
            };

            var response = await SendRequestAsync(
                requestMessage,
                "submit_job_result",
                TimeSpan.FromMilliseconds(timeoutMs),
                token);

            var status = JsonHelpers.GetString(response, "status");
            var jobId = JsonHelpers.GetString(response, "job_id");
            if (!string.Equals(status, "accepted", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(jobId))
            {
                throw new McpException(
                    ErrorCodes.InvalidResponse,
                    "Invalid submit_job_result payload",
                    JsonHelpers.CloneNode(response));
            }

            UpsertJob(jobId, JobState.Queued, null);

            return new RunTestsResult(jobId, WireState.ToWire(JobState.Queued));
        }, cancellationToken);
    }

    public Task<JobStatusResult> GetJobStatusAsync(JobStatusRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            var jobId = request.JobId;
            if (!_jobs.ContainsKey(jobId))
            {
                throw new McpException(ErrorCodes.JobNotFound, $"Unknown job_id: {jobId}");
            }

            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetJobStatus);

            var requestMessage = new JsonObject
            {
                ["type"] = ToolNames.GetJobStatus,
                ["protocol_version"] = Constants.ProtocolVersion,
                ["request_id"] = Guid.NewGuid().ToString("D"),
                ["job_id"] = jobId,
            };

            var response = await SendRequestAsync(
                requestMessage,
                "job_status",
                TimeSpan.FromMilliseconds(timeoutMs),
                token);

            var stateRaw = JsonHelpers.GetString(response, "state");
            if (!WireState.TryParseJobState(stateRaw, out var state))
            {
                throw new McpException(
                    ErrorCodes.InvalidResponse,
                    "Invalid job_status.state value",
                    JsonHelpers.CloneNode(response));
            }

            var result = JsonHelpers.CloneNode(response["result"]) ?? new JsonObject();
            UpsertJob(jobId, state, result);

            return new JobStatusResult(jobId, state.ToWire(), null, result);
        }, cancellationToken);
    }

    public Task<CancelJobResult> CancelJobAsync(CancelJobRequest request, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            PruneJobs();
            var jobId = request.JobId;
            if (!_jobs.TryGetValue(jobId, out var existing))
            {
                throw new McpException(ErrorCodes.JobNotFound, $"Unknown job_id: {jobId}");
            }

            if (existing.State == JobState.Queued)
            {
                UpsertJob(jobId, JobState.Cancelled, existing.Result);
                return new CancelJobResult(jobId, "cancelled");
            }

            if (WireState.IsTerminal(existing.State))
            {
                return new CancelJobResult(jobId, "rejected");
            }

            await EnsureEditorReadyAsync(token);
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.CancelJob);

            var requestMessage = new JsonObject
            {
                ["type"] = "cancel",
                ["protocol_version"] = Constants.ProtocolVersion,
                ["request_id"] = Guid.NewGuid().ToString("D"),
                ["target_job_id"] = jobId,
            };

            var response = await SendRequestAsync(
                requestMessage,
                "cancel_result",
                TimeSpan.FromMilliseconds(timeoutMs),
                token);

            var status = JsonHelpers.GetString(response, "status");
            if (status is not ("cancelled" or "cancel_requested" or "rejected"))
            {
                throw new McpException(
                    ErrorCodes.InvalidResponse,
                    "Invalid cancel_result.status value",
                    JsonHelpers.CloneNode(response));
            }

            if (string.Equals(status, "cancelled", StringComparison.Ordinal))
            {
                UpsertJob(jobId, JobState.Cancelled, existing.Result);
            }

            return new CancelJobResult(jobId, status);
        }, cancellationToken);
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult receiveResult;

                do
                {
                    receiveResult = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (receiveResult.Count > 0)
                    {
                        stream.Write(buffer, 0, receiveResult.Count);
                    }

                    if (stream.Length > Constants.MaxMessageBytes)
                    {
                        await SafeCloseSocketAsync(socket, WebSocketCloseStatus.MessageTooBig, "message-too-large", cancellationToken);
                        return;
                    }
                }
                while (!receiveResult.EndOfMessage);

                if (receiveResult.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var raw = Encoding.UTF8.GetString(stream.ToArray());
                await OnSocketMessageAsync(socket, raw, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown path
        }
        catch (WebSocketException ex)
        {
            if (!IsShuttingDown() && !IsExpectedDisconnect(ex))
            {
                Logger.Warn("Unity websocket receive error", ("error", ex.Message));
            }
        }
        catch (ObjectDisposedException)
        {
            // socket disposed during reconnect/shutdown
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task OnSocketMessageAsync(WebSocket sourceSocket, string raw, CancellationToken cancellationToken)
    {
        JsonObject message;
        try
        {
            var node = JsonNode.Parse(raw);
            message = node as JsonObject ?? throw new JsonException("Message is not an object");
        }
        catch (Exception)
        {
            Logger.Warn("Received non-JSON message from Unity");
            return;
        }

        var protocolVersion = JsonHelpers.GetInt(message, "protocol_version");
        if (protocolVersion.HasValue && protocolVersion.Value != Constants.ProtocolVersion)
        {
            var errorPayload = new JsonObject
            {
                ["type"] = "error",
                ["protocol_version"] = Constants.ProtocolVersion,
                ["request_id"] = JsonHelpers.CloneNode(message["request_id"]),
                ["error"] = new JsonObject
                {
                    ["code"] = ErrorCodes.InvalidRequest,
                    ["message"] = "protocol_version mismatch",
                },
            };

            await SendRawAsync(sourceSocket, errorPayload, cancellationToken);
            await SafeCloseSocketAsync(sourceSocket, WebSocketCloseStatus.ProtocolError, "protocol-version-mismatch", cancellationToken);
            return;
        }

        var type = JsonHelpers.GetString(message, "type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        if (!string.Equals(type, "hello", StringComparison.Ordinal) && !_sessionRegistry.IsActive(sourceSocket))
        {
            Logger.Warn("Received message from non-active Unity websocket", ("type", type));
            await SafeCloseSocketAsync(sourceSocket, WebSocketCloseStatus.PolicyViolation, "inactive-session", cancellationToken);
            return;
        }

        if (_sessionRegistry.IsActive(sourceSocket))
        {
            RecordMessageReceived();
        }

        switch (type)
        {
            case "hello":
                await HandleHelloAsync(sourceSocket, message, cancellationToken);
                return;
            case "editor_status":
                HandleEditorStatus(message);
                return;
        }

        var requestId = JsonHelpers.GetString(message, "request_id");
        if (!string.IsNullOrWhiteSpace(requestId) && _pendingRequests.TryRemove(requestId, out var pending))
        {
            try
            {
                if (string.Equals(type, "error", StringComparison.Ordinal))
                {
                    var error = JsonHelpers.AsObjectOrEmpty(message["error"]);
                    pending.Completion.TrySetException(new McpException(
                        JsonHelpers.GetString(error, "code") ?? ErrorCodes.UnityExecution,
                        JsonHelpers.GetString(error, "message") ?? "Unity returned error",
                        JsonHelpers.CloneNode(error["details"])));

                    return;
                }

                if (!string.Equals(type, pending.ExpectedType, StringComparison.Ordinal))
                {
                    pending.Completion.TrySetException(new McpException(
                        ErrorCodes.InvalidResponse,
                        "Unexpected response type",
                        new JsonObject
                        {
                            ["expected"] = pending.ExpectedType,
                            ["actual"] = type,
                        }));

                    return;
                }

                pending.Completion.TrySetResult(message);
            }
            finally
            {
                pending.Dispose();
            }

            return;
        }

        Logger.Warn("Unhandled message from Unity", ("type", type), ("request_id", requestId));
    }

    private async Task HandleHelloAsync(WebSocket socket, JsonObject message, CancellationToken cancellationToken)
    {
        var editorInstanceId = JsonHelpers.GetString(message, "editor_instance_id");
        var pluginSessionId = JsonHelpers.GetString(message, "plugin_session_id");
        var connectAttemptSeq = JsonHelpers.GetUlong(message, "connect_attempt_seq");
        if (string.IsNullOrWhiteSpace(editorInstanceId) ||
            string.IsNullOrWhiteSpace(pluginSessionId) ||
            !connectAttemptSeq.HasValue)
        {
            await SendRawAsync(socket, new JsonObject
            {
                ["type"] = "error",
                ["protocol_version"] = Constants.ProtocolVersion,
                ["request_id"] = JsonHelpers.CloneNode(message["request_id"]),
                ["error"] = new JsonObject
                {
                    ["code"] = ErrorCodes.InvalidRequest,
                    ["message"] = "hello requires editor_instance_id, plugin_session_id, connect_attempt_seq",
                },
            }, cancellationToken);

            await SafeCloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "invalid-hello", cancellationToken);
            return;
        }

        var acceptance = _sessionRegistry.TryAccept(socket, editorInstanceId);
        if (acceptance.Result == AcceptResult.Rejected)
        {
            LogRejectedActiveSessionWarning(editorInstanceId, pluginSessionId, connectAttemptSeq);
            await SendRawAsync(socket, new JsonObject
            {
                ["type"] = "error",
                ["protocol_version"] = Constants.ProtocolVersion,
                ["request_id"] = JsonHelpers.CloneNode(message["request_id"]),
                ["error"] = new JsonObject
                {
                    ["code"] = ErrorCodes.InvalidRequest,
                    ["message"] = "another Unity websocket session is already active",
                },
            }, cancellationToken);

            await SafeCloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "session-already-active", cancellationToken);
            return;
        }

        if (acceptance.Result == AcceptResult.Replaced && acceptance.ReplacedSocket is not null)
        {
            await ReplaceActiveSessionAsync(acceptance.ReplacedSocket);
        }

        var connectionId = Guid.NewGuid().ToString("D");
        var initialState = WireState.ParseEditorState(JsonHelpers.GetString(message, "state"));

        await SendRawAsync(socket, new JsonObject
        {
            ["type"] = "hello",
            ["protocol_version"] = Constants.ProtocolVersion,
            ["server_version"] = Constants.ServerVersion,
            ["connection_id"] = connectionId,
            ["editor_status_interval_ms"] = Constants.EditorStatusIntervalMs,
        }, cancellationToken);

        await SendRawAsync(socket, new JsonObject
        {
            ["type"] = "capability",
            ["protocol_version"] = Constants.ProtocolVersion,
            ["tools"] = ToolCatalog.BuildUnityCapabilityTools(),
        }, cancellationToken);

        RecordMessageReceived();
        _runtimeState.OnConnected(initialState, connectionId, editorInstanceId);
        StartStaleTimer(socket, connectionId, editorInstanceId);

        Logger.Info(
            "Unity websocket session activated",
            ("connection_id", connectionId),
            ("editor_instance_id", editorInstanceId),
            ("plugin_session_id", pluginSessionId),
            ("connect_attempt_seq", connectAttemptSeq),
            ("editor_state", initialState.ToWire()));
    }

    private async Task ReplaceActiveSessionAsync(WebSocket replacedSocket)
    {
        var snapshot = _runtimeState.GetSnapshot();
        StopStaleTimer();
        FailPendingRequestsAsDisconnected();
        _runtimeState.OnDisconnected();
        await SafeCloseSocketAsync(replacedSocket, WebSocketCloseStatus.NormalClosure, "replaced-by-same-editor", CancellationToken.None);
        Logger.Info(
            "Unity websocket session replaced existing active session for same editor",
            ("replaced_connection_id", snapshot.ActiveConnectionId),
            ("editor_instance_id", snapshot.EditorInstanceId));
    }

    private void HandleEditorStatus(JsonObject message)
    {
        var state = WireState.ParseEditorState(JsonHelpers.GetString(message, "state"));
        var seq = JsonHelpers.GetUlong(message, "seq") ?? 0;
        _runtimeState.OnEditorStatus(state, seq);
    }

    private void RecordMessageReceived()
    {
        Interlocked.Exchange(ref _lastMessageReceivedAtUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        _runtimeState.RecordMessageReceived();
    }

    private void StartStaleTimer(WebSocket socket, string connectionId, string? editorInstanceId)
    {
        StopStaleTimer();
        var staleTimerCts = new CancellationTokenSource();
        _staleTimerCts = staleTimerCts;
        var checkIntervalMs = Constants.StaleConnectionTimeoutMs / 3;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!staleTimerCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(checkIntervalMs, staleTimerCts.Token);

                    if (socket.State != WebSocketState.Open)
                    {
                        return;
                    }

                    var lastTicks = Interlocked.Read(ref _lastMessageReceivedAtUtcTicks);
                    var elapsed = DateTimeOffset.UtcNow.UtcTicks - lastTicks;
                    if (elapsed > TimeSpan.FromMilliseconds(Constants.StaleConnectionTimeoutMs).Ticks)
                    {
                        Logger.Warn(
                            "Stale connection timeout reached. Closing Unity websocket.",
                            ("connection_id", connectionId),
                            ("editor_instance_id", editorInstanceId),
                            ("timeout_ms", Constants.StaleConnectionTimeoutMs));
                        await SafeCloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "stale-connection-timeout", CancellationToken.None);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            catch (Exception ex)
            {
                Logger.Warn("Stale timer loop error", ("error", ex.Message));
            }
        });
    }

    private void StopStaleTimer()
    {
        var cts = Interlocked.Exchange(ref _staleTimerCts, null);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task<JsonObject> SendRequestAsync(JsonObject message, string expectedType, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var requestId = JsonHelpers.GetString(message, "request_id");
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new McpException(ErrorCodes.InvalidRequest, "request_id is required");
        }

        var stage = DispatchStage.BeforeSend;
        WebSocket socket;
        try
        {
            socket = GetOpenSocketOrThrow();
        }
        catch (McpException ex)
        {
            throw ErrorSemanticsResolver.NormalizeDispatchFailure(ex, stage);
        }

        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var pending = new PendingRequest(expectedType, completion, timeoutCts);
        if (!_pendingRequests.TryAdd(requestId, pending))
        {
            pending.Dispose();
            throw new McpException(ErrorCodes.InvalidRequest, $"Duplicate request_id: {requestId}");
        }

        using var timeoutRegistration = timeoutCts.Token.Register(() =>
        {
            if (_pendingRequests.TryRemove(requestId, out var timedOutPending))
            {
                timedOutPending.Completion.TrySetException(new McpException(
                    ErrorCodes.RequestTimeout,
                    $"Unity request timeout: {requestId}"));
                timedOutPending.Dispose();
            }
        });

        try
        {
            await SendRawAsync(socket, message, cancellationToken);
            stage = DispatchStage.AfterSend;
            var response = await completion.Task.WaitAsync(cancellationToken);
            stage = DispatchStage.Completed;
            return response;
        }
        catch (McpException ex)
        {
            throw ErrorSemanticsResolver.NormalizeDispatchFailure(ex, stage);
        }
        finally
        {
            if (_pendingRequests.TryRemove(requestId, out var remaining))
            {
                remaining.Dispose();
            }
        }
    }

    private async Task EnsureEditorReadyAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsEditorReady())
        {
            return;
        }

        var waitPolicy = ResolveEditorReadyWaitPolicy(_runtimeState.GetSnapshot());
        var resumed = await _runtimeState.WaitForEditorReadyAsync(
            waitPolicy.Timeout,
            cancellationToken);

        if (!resumed)
        {
            throw new McpException(waitPolicy.TimeoutErrorCode, waitPolicy.TimeoutErrorMessage);
        }
    }

    internal static EditorReadyWaitPolicy ResolveEditorReadyWaitPolicy(RuntimeSnapshot snapshot)
    {
        if (snapshot.WaitingReason is "compiling" or "reloading")
        {
            return new EditorReadyWaitPolicy(
                TimeSpan.FromMilliseconds(Constants.CompileGraceTimeoutMs),
                ErrorCodes.CompileTimeout,
                "Editor did not become ready within compile/reload grace timeout");
        }

        return new EditorReadyWaitPolicy(
            TimeSpan.FromMilliseconds(Constants.RequestReconnectWaitMs),
            ErrorCodes.EditorNotReady,
            "Editor is not ready");
    }

    private WebSocket GetOpenSocketOrThrow()
    {
        var socket = _sessionRegistry.GetActiveSocket();
        if (socket is null)
        {
            throw new McpException(ErrorCodes.UnityDisconnected, "Unity websocket is not connected");
        }

        return socket;
    }

    private void OnSocketClosed(WebSocket closedSocket)
    {
        var wasActive = _sessionRegistry.Remove(closedSocket);
        if (!wasActive)
        {
            return;
        }

        var snapshot = _runtimeState.GetSnapshot();
        var wasConnected = snapshot.Connected;
        StopStaleTimer();
        FailPendingRequestsAsDisconnected();
        _runtimeState.OnDisconnected();
        if (wasConnected && !IsShuttingDown())
        {
            Logger.Info(
                "Unity websocket session disconnected",
                ("connection_id", snapshot.ActiveConnectionId),
                ("editor_instance_id", snapshot.EditorInstanceId));
        }
    }

    private void FailPendingRequestsAsDisconnected()
    {
        foreach (var (requestId, pending) in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(requestId, out var removed))
            {
                removed.Completion.TrySetException(new McpException(
                    ErrorCodes.UnityDisconnected,
                    "Unity websocket disconnected"));
                removed.Dispose();
            }
        }
    }

    private bool IsShuttingDown()
    {
        return Volatile.Read(ref _shutdownRequested) != 0;
    }

    private static bool IsExpectedDisconnect(WebSocketException ex)
    {
        if (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            return true;
        }

        var message = ex.Message ?? string.Empty;
        return message.IndexOf("without completing the close handshake", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void UpsertJob(string jobId, JobState state, JsonNode? result)
    {
        _jobs[jobId] = new JobRecord(state, JsonHelpers.CloneNode(result), DateTimeOffset.UtcNow);
        PruneJobs();
    }

    private void LogRejectedActiveSessionWarning(string? editorInstanceId, string? pluginSessionId, ulong? connectAttemptSeq)
    {
        int suppressed = 0;
        var shouldEmit = false;
        var now = DateTimeOffset.UtcNow;
        lock (_activeSessionRejectLogGate)
        {
            var sinceLast = now - _lastActiveSessionRejectLogUtc;
            if (_lastActiveSessionRejectLogUtc != default && sinceLast < ActiveSessionRejectLogThrottle)
            {
                _suppressedActiveSessionRejectLogs += 1;
                return;
            }

            shouldEmit = true;
            suppressed = _suppressedActiveSessionRejectLogs;
            _suppressedActiveSessionRejectLogs = 0;
            _lastActiveSessionRejectLogUtc = now;
        }

        if (!shouldEmit)
        {
            return;
        }

        if (suppressed > 0)
        {
            Logger.Warn(
                "Rejected Unity websocket session because another editor is active",
                ("suppressed", suppressed),
                ("throttle_sec", (int)ActiveSessionRejectLogThrottle.TotalSeconds),
                ("editor_instance_id", editorInstanceId),
                ("plugin_session_id", pluginSessionId),
                ("connect_attempt_seq", connectAttemptSeq));
            return;
        }

        Logger.Warn(
            "Rejected Unity websocket session because another editor is active",
            ("editor_instance_id", editorInstanceId),
            ("plugin_session_id", pluginSessionId),
            ("connect_attempt_seq", connectAttemptSeq));
    }

    private void PruneJobs()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (jobId, record) in _jobs)
        {
            if (!WireState.IsTerminal(record.State))
            {
                continue;
            }

            if (now - record.UpdatedAt <= JobRetention)
            {
                continue;
            }

            _jobs.TryRemove(jobId, out _);
        }

        var count = _jobs.Count;
        if (count <= MaxRetainedJobs)
        {
            return;
        }

        var overflow = count - MaxRetainedJobs;
        var removable = new List<KeyValuePair<string, JobRecord>>();
        foreach (var pair in _jobs)
        {
            if (!WireState.IsTerminal(pair.Value.State))
            {
                continue;
            }

            removable.Add(pair);
        }

        if (removable.Count == 0)
        {
            return;
        }

        removable.Sort(static (left, right) => left.Value.UpdatedAt.CompareTo(right.Value.UpdatedAt));
        var removeCount = Math.Min(overflow, removable.Count);
        for (var i = 0; i < removeCount; i += 1)
        {
            _jobs.TryRemove(removable[i].Key, out _);
        }
    }

    private static async Task SendRawAsync(WebSocket socket, JsonObject payload, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            throw new McpException(ErrorCodes.UnityDisconnected, "Unity websocket is not connected");
        }

        var json = payload.ToJsonString(JsonDefaults.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task SafeCloseSocketAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Closed or WebSocketState.Aborted)
        {
            return;
        }

        try
        {
            await socket.CloseAsync(status, description, cancellationToken);
        }
        catch
        {
            // no-op
        }
    }
}
