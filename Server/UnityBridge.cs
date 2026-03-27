using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace UnityMcpServer;

internal readonly record struct EditorReadyWaitPolicy(TimeSpan Timeout, string TimeoutErrorCode, string TimeoutErrorMessage);

internal sealed class UnityBridge
{
    private static readonly TimeSpan LifecycleLogThrottleInterval = TimeSpan.FromSeconds(30);

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

    private readonly RuntimeState _runtimeState;
    private readonly ILogger<UnityBridge> _logger;
    /// <summary>multi-step workflow（refresh_assets, control_play_mode, run_tests, manage_build, manage_asmdef mutation）の排他実行ロック。</summary>
    private readonly SemaphoreSlim _workflowLock = new(1, 1);
    /// <summary>WebSocket フレームのインターリーブを防止する送信ロック。</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly UnitySessionRegistry _sessionRegistry = new();
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new(StringComparer.Ordinal);
    private readonly LogThrottle _sessionConnectLog = new(LifecycleLogThrottleInterval);
    private readonly LogThrottle _sessionDisconnectLog = new(LifecycleLogThrottleInterval);
    private readonly LogThrottle _sessionRejectLog = new(LifecycleLogThrottleInterval);

    private CancellationTokenSource? _staleTimerCts;
    private long _lastMessageReceivedAtUtcTicks;
    private int _shutdownRequested;
    /// <summary>初回接続を判定するフラグ。connect_attempt_seq は domain reload でリセットされるため使用不可。</summary>
    private int _hasEverConnected;

    public UnityBridge(RuntimeState runtimeState, ILogger<UnityBridge> logger)
    {
        _runtimeState = runtimeState;
        _logger = logger;
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
                _logger.ZLogWarning($"Unity websocket session failed error={ex.Message}");
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

    public async Task<ReadConsoleResult> ReadConsoleAsync(ReadConsoleRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ReadConsole);
        var parameters = new JsonObject
        {
            ["max_entries"] = request.MaxEntries,
            ["stack_trace_lines"] = request.StackTraceLines,
            ["deduplicate"] = request.Deduplicate,
            ["offset"] = request.Offset,
        };
        if (request.LogType is { Length: > 0 })
        {
            parameters["log_type"] = new JsonArray(request.LogType.Select(t => (JsonNode)t).ToArray());
        }
        if (request.MessagePattern is not null)
        {
            parameters["message_pattern"] = request.MessagePattern;
        }
        var payload = await ExecuteSyncToolAsync(
            ToolNames.ReadConsole,
            parameters,
            timeoutMs,
            cancellationToken);
        return new ReadConsoleResult(payload);
    }

    public async Task<GetPlayModeStateResult> GetPlayModeStateAsync(CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetPlayModeState);
        var payload = await ExecuteSyncToolAsync(ToolNames.GetPlayModeState, new JsonObject(), timeoutMs, cancellationToken);
        return new GetPlayModeStateResult(payload);
    }

    public async Task<ClearConsoleResult> ClearConsoleAsync(CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ClearConsole);
        var payload = await ExecuteSyncToolAsync(ToolNames.ClearConsole, new JsonObject(), timeoutMs, cancellationToken);
        return new ClearConsoleResult(payload);
    }

    public async Task<RefreshAssetsResult> RefreshAssetsAsync(bool force, CancellationToken cancellationToken)
    {
        // App Nap による遅延を防ぐため、操作前に Editor を前面に出す
        await using var focusScope = await EditorFocusScope.ActivateAsync(
            _runtimeState.GetEditorPid());

        await EnsureEditorReadyAsync(cancellationToken);
        await _workflowLock.WaitAsync(cancellationToken);
        try
        {
            // lock 待機中に状態が変わりうるため再チェック
            await EnsureEditorReadyAsync(cancellationToken);

            // Play モード中は AssetDatabase.Refresh が動作しないため、先に停止する
            await EnsureEditModeAsync(cancellationToken);

            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.RefreshAssets);
            var toolParams = new JsonObject();
            if (force) toolParams["force"] = true;

            JsonNode payload;
            try
            {
                payload = await ExecuteSyncToolAsync(
                    ToolNames.RefreshAssets, toolParams, timeoutMs, cancellationToken);
            }
            catch (McpException ex) when (ex.Code == ErrorCodes.ReconnectTimeout)
            {
                // ドメインリロードでレスポンス前に切断。Refresh は実行済み。
                await EnsureEditorReadyAsync(cancellationToken);
                payload = new JsonObject { ["refreshed"] = true, ["compiling"] = true };
            }

            // Plugin の3分岐: compiling / compilation_failed / neither
            var compiling = payload is JsonObject po
                && (JsonHelpers.GetBool(po, "compiling") ?? false);
            var compilationFailed = payload is JsonObject pf
                && (JsonHelpers.GetBool(pf, "compilation_failed") ?? false);

            // 内部フラグを除去
            if (payload is JsonObject obj)
            {
                obj.Remove("compiling");
                obj.Remove("compilation_failed");
            }

            if (compiling)
            {
                // コンパイル中 → ポーリングで完了待ち → エラー取得
                await WaitForCompilationAsync(payload, cancellationToken);
            }
            else if (compilationFailed)
            {
                // 既存のコンパイルエラー → エラー取得のみ
                await AppendErrorsIfAny(payload, cancellationToken);
            }
            else
            {
                // ドメインリロード検知フォールバック:
                // Refresh() 後に compiling=false でも、コンパイル成功→ドメインリロードが
                // 遅延発生するケースがある。500ms 後に1回確認ポーリングを行う。
                await CheckForDelayedCompilationAsync(payload, cancellationToken);
            }

            return new RefreshAssetsResult(payload);
        }
        finally
        {
            _workflowLock.Release();
        }
    }

    /// <summary>
    /// Play モードがアクティブなら停止し、Edit モードに遷移してから戻る。
    /// AssetDatabase.Refresh は Play モード中に動作しないため、refresh_assets の前処理として使う。
    /// 呼び出し元は EnsureEditorReadyAsync 済みであることを前提とする。
    /// 停止した場合 true を返す。
    /// </summary>
    private async Task<bool> EnsureEditModeAsync(CancellationToken token)
    {
        var getStateTimeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetPlayModeState);
        var statePayload = await ExecuteSyncToolAsync(
            ToolNames.GetPlayModeState, new JsonObject(), getStateTimeoutMs, token);

        // is_playing が true なら paused 含め Play 中。
        // EnsureEditorReadyAsync が先に呼ばれているため、遷移中 (EnteringPlayMode 等) はここに到達しない。
        var isPlaying = statePayload is JsonObject obj
            && (JsonHelpers.GetBool(obj, "is_playing") ?? false);

        if (!isPlaying)
            return false;

        // Play モード停止。ドメインリロード発生時は ReconnectTimeout になる。
        // ControlPlayModeAsync と同じリカバリパターン。
        var stopTimeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ControlPlayMode);
        try
        {
            await ExecuteSyncToolAsync(
                ToolNames.ControlPlayMode,
                new JsonObject { ["action"] = PlayModeActions.Stop },
                stopTimeoutMs,
                token);
        }
        catch (McpException ex) when (ex.Code == ErrorCodes.ReconnectTimeout)
        {
            // Play モード終了時のドメインリロードで Plugin が切断される。想定内。
        }

        // ドメインリロード発生時は再接続を待つ。
        // 未発生時は IsEditorReady() == true で即座に返る。
        // いずれの場合も、後続の refresh_assets は Plugin 側メインスレッドで実行されるため、
        // Play モード遷移は完了済みとなる。
        await EnsureEditorReadyAsync(token);
        return true;
    }

    public async Task<ControlPlayModeResult> ControlPlayModeAsync(ControlPlayModeRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        await _workflowLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureEditorReadyAsync(cancellationToken);

            // Play Mode 開始前にコンパイルエラーがないことを確認する。
            // EditorApplication.isPlaying = true はリクエストであり、コンパイルエラーがあると
            // Unity が黙って拒否するため、サーバー側で事前検証して明確なエラーを返す。
            if (string.Equals(request.Action, PlayModeActions.Start, StringComparison.Ordinal))
            {
                await ThrowIfCompileErrorsAsync(cancellationToken);
            }

            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ControlPlayMode);
            try
            {
                var payload = await ExecuteSyncToolAsync(
                    ToolNames.ControlPlayMode,
                    new JsonObject
                    {
                        ["action"] = request.Action,
                    },
                    timeoutMs,
                    cancellationToken);
                return new ControlPlayModeResult(payload);
            }
            catch (McpException ex) when (ex.Code == ErrorCodes.ReconnectTimeout &&
                                          string.Equals(request.Action, PlayModeActions.Start, StringComparison.Ordinal))
            {
                await EnsureEditorReadyAsync(cancellationToken);
                return new ControlPlayModeResult(new JsonObject
                {
                    ["action"] = request.Action,
                    ["accepted"] = true,
                    ["is_playing"] = true,
                    ["is_paused"] = false,
                    ["is_playing_or_will_change_playmode"] = true,
                });
            }
            catch (McpException ex) when (ex.Code == ErrorCodes.ReconnectTimeout &&
                                          string.Equals(request.Action, PlayModeActions.Stop, StringComparison.Ordinal))
            {
                await EnsureEditorReadyAsync(cancellationToken);
                return new ControlPlayModeResult(new JsonObject
                {
                    ["action"] = request.Action,
                    ["accepted"] = true,
                    ["is_playing"] = false,
                    ["is_paused"] = false,
                    ["is_playing_or_will_change_playmode"] = false,
                });
            }
        }
        finally
        {
            _workflowLock.Release();
        }
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

        // プラグインはエラーを result オブジェクト内に { code, message } として返す
        var errorResult = JsonHelpers.AsObjectOrEmpty(JsonHelpers.CloneNode(response["result"]));
        var pluginErrorCode = JsonHelpers.GetString(errorResult, "code");
        var pluginMessage = JsonHelpers.GetString(errorResult, "message");

        // プラグイン固有のエラーコードをそのまま転送（LLM が具体的な原因を判断できるようにする）
        var errorCode = !string.IsNullOrWhiteSpace(pluginErrorCode)
            ? pluginErrorCode
            : ErrorCodes.UnityExecution;

        var wrappedDetails = new JsonObject();
        if (!string.IsNullOrWhiteSpace(pluginErrorCode))
        {
            wrappedDetails["plugin_error_code"] = pluginErrorCode;
        }

        throw new McpException(
            errorCode,
            pluginMessage ?? "Unity returned execution error",
            wrappedDetails);
    }

    public async Task<GetSceneHierarchyResult> GetSceneHierarchyAsync(GetSceneHierarchyRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetSceneHierarchy);
        var parameters = new JsonObject
        {
            ["max_depth"] = request.MaxDepth,
            ["max_game_objects"] = request.MaxGameObjects,
            ["offset"] = request.Offset,
        };
        if (!string.IsNullOrWhiteSpace(request.RootPath))
        {
            parameters["root_path"] = request.RootPath;
        }
        if (request.ComponentFilter is { Length: > 0 })
        {
            parameters["component_filter"] = new JsonArray(request.ComponentFilter.Select(s => (JsonNode)s).ToArray());
        }

        var payload = await ExecuteSyncToolAsync(ToolNames.GetSceneHierarchy, parameters, timeoutMs, cancellationToken);
        return new GetSceneHierarchyResult(payload);
    }

    public async Task<GetSceneComponentInfoResult> GetSceneComponentInfoAsync(GetSceneComponentInfoRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetSceneComponentInfo);
        var parameters = new JsonObject
        {
            ["game_object_path"] = request.GameObjectPath,
            ["max_array_elements"] = request.MaxArrayElements,
        };
        if (request.Index.HasValue)
        {
            parameters["index"] = request.Index.Value;
        }

        if (request.Fields is not null)
        {
            var fieldsArray = new JsonArray();
            foreach (var f in request.Fields)
            {
                fieldsArray.Add(f);
            }

            parameters["fields"] = fieldsArray;
        }

        var payload = await ExecuteSyncToolAsync(ToolNames.GetSceneComponentInfo, parameters, timeoutMs, cancellationToken);
        return new GetSceneComponentInfoResult(payload);
    }

    public async Task<ManageSceneComponentResult> ManageSceneComponentAsync(ManageSceneComponentRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
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

        var payload = await ExecuteSyncToolAsync(ToolNames.ManageSceneComponent, parameters, timeoutMs, cancellationToken);
        return new ManageSceneComponentResult(payload);
    }

    public async Task<GetPrefabHierarchyResult> GetPrefabHierarchyAsync(GetPrefabHierarchyRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetPrefabHierarchy);
        var parameters = new JsonObject
        {
            ["prefab_path"] = request.PrefabPath,
            ["max_depth"] = request.MaxDepth,
            ["max_game_objects"] = request.MaxGameObjects,
            ["offset"] = request.Offset,
        };
        if (!string.IsNullOrWhiteSpace(request.GameObjectPath))
        {
            parameters["game_object_path"] = request.GameObjectPath;
        }
        if (request.ComponentFilter is { Length: > 0 })
        {
            parameters["component_filter"] = new JsonArray(request.ComponentFilter.Select(s => (JsonNode)s).ToArray());
        }

        var payload = await ExecuteSyncToolAsync(ToolNames.GetPrefabHierarchy, parameters, timeoutMs, cancellationToken);
        return new GetPrefabHierarchyResult(payload);
    }

    public async Task<GetPrefabComponentInfoResult> GetPrefabComponentInfoAsync(GetPrefabComponentInfoRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetPrefabComponentInfo);
        var parameters = new JsonObject
        {
            ["prefab_path"] = request.PrefabPath,
            ["game_object_path"] = request.GameObjectPath,
            ["max_array_elements"] = request.MaxArrayElements,
        };
        if (request.Index.HasValue)
        {
            parameters["index"] = request.Index.Value;
        }

        if (request.Fields is not null)
        {
            var fieldsArray = new JsonArray();
            foreach (var f in request.Fields)
            {
                fieldsArray.Add(f);
            }

            parameters["fields"] = fieldsArray;
        }

        var payload = await ExecuteSyncToolAsync(ToolNames.GetPrefabComponentInfo, parameters, timeoutMs, cancellationToken);
        return new GetPrefabComponentInfoResult(payload);
    }

    public async Task<ManagePrefabComponentResult> ManagePrefabComponentAsync(ManagePrefabComponentRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
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

        var payload = await ExecuteSyncToolAsync(ToolNames.ManagePrefabComponent, parameters, timeoutMs, cancellationToken);
        return new ManagePrefabComponentResult(payload);
    }

    public async Task<ManageSceneGameObjectResult> ManageSceneGameObjectAsync(ManageSceneGameObjectRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
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

        var payload = await ExecuteSyncToolAsync(ToolNames.ManageSceneGameObject, parameters, timeoutMs, cancellationToken);
        return new ManageSceneGameObjectResult(payload);
    }

    public async Task<ManagePrefabGameObjectResult> ManagePrefabGameObjectAsync(ManagePrefabGameObjectRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
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

        var payload = await ExecuteSyncToolAsync(ToolNames.ManagePrefabGameObject, parameters, timeoutMs, cancellationToken);
        return new ManagePrefabGameObjectResult(payload);
    }

    public async Task<ListScenesResult> ListScenesAsync(ListScenesRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ListScenes);
        var parameters = new JsonObject
        {
            ["max_results"] = request.MaxResults,
            ["offset"] = request.Offset,
        };
        if (request.NamePattern is not null)
        {
            parameters["name_pattern"] = request.NamePattern;
        }
        var payload = await ExecuteSyncToolAsync(ToolNames.ListScenes, parameters, timeoutMs, cancellationToken);
        return new ListScenesResult(payload);
    }

    public async Task<OpenSceneResult> OpenSceneAsync(OpenSceneRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.OpenScene);
        var parameters = new JsonObject
        {
            ["path"] = request.Path,
            ["mode"] = request.Mode,
        };
        var payload = await ExecuteSyncToolAsync(ToolNames.OpenScene, parameters, timeoutMs, cancellationToken);
        return new OpenSceneResult(payload);
    }

    public async Task<SaveSceneResult> SaveSceneAsync(SaveSceneRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.SaveScene);
        var parameters = new JsonObject();
        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            parameters["path"] = request.Path;
        }

        var payload = await ExecuteSyncToolAsync(ToolNames.SaveScene, parameters, timeoutMs, cancellationToken);
        return new SaveSceneResult(payload);
    }

    public async Task<CreateSceneResult> CreateSceneAsync(CreateSceneRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.CreateScene);
        var parameters = new JsonObject
        {
            ["path"] = request.Path,
            ["setup"] = request.Setup,
        };
        var payload = await ExecuteSyncToolAsync(ToolNames.CreateScene, parameters, timeoutMs, cancellationToken);
        return new CreateSceneResult(payload);
    }

    public async Task<FindAssetsResult> FindAssetsAsync(FindAssetsRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.FindAssets);
        var parameters = new JsonObject
        {
            ["filter"] = request.Filter,
            ["max_results"] = request.MaxResults,
            ["offset"] = request.Offset,
        };
        if (request.SearchInFolders is not null)
        {
            var foldersArray = new JsonArray();
            foreach (var folder in request.SearchInFolders)
            {
                foldersArray.Add(folder);
            }

            parameters["search_in_folders"] = foldersArray;
        }

        var payload = await ExecuteSyncToolAsync(ToolNames.FindAssets, parameters, timeoutMs, cancellationToken);
        return new FindAssetsResult(payload);
    }

    public async Task<FindSceneGameObjectsResult> FindSceneGameObjectsAsync(FindSceneGameObjectsRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.FindSceneGameObjects);
        var parameters = new JsonObject();
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            parameters["name"] = request.Name;
        }

        if (!string.IsNullOrWhiteSpace(request.Tag))
        {
            parameters["tag"] = request.Tag;
        }

        if (!string.IsNullOrWhiteSpace(request.ComponentType))
        {
            parameters["component_type"] = request.ComponentType;
        }

        if (!string.IsNullOrWhiteSpace(request.RootPath))
        {
            parameters["root_path"] = request.RootPath;
        }

        if (request.Layer.HasValue)
        {
            parameters["layer"] = request.Layer.Value;
        }

        if (request.Active.HasValue)
        {
            parameters["active"] = request.Active.Value;
        }

        parameters["max_results"] = request.MaxResults;
        parameters["offset"] = request.Offset;
        var payload = await ExecuteSyncToolAsync(ToolNames.FindSceneGameObjects, parameters, timeoutMs, cancellationToken);
        return new FindSceneGameObjectsResult(payload);
    }

    public async Task<InstantiatePrefabResult> InstantiatePrefabAsync(InstantiatePrefabRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.InstantiatePrefab);
        var parameters = new JsonObject
        {
            ["prefab_path"] = request.PrefabPath,
        };
        if (!string.IsNullOrWhiteSpace(request.ParentPath))
        {
            parameters["parent_path"] = request.ParentPath;
        }

        if (request.Position is not null)
        {
            parameters["position"] = request.Position.DeepClone();
        }

        if (request.Rotation is not null)
        {
            parameters["rotation"] = request.Rotation.DeepClone();
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            parameters["name"] = request.Name;
        }

        if (request.SiblingIndex.HasValue)
        {
            parameters["sibling_index"] = request.SiblingIndex.Value;
        }

        var payload = await ExecuteSyncToolAsync(ToolNames.InstantiatePrefab, parameters, timeoutMs, cancellationToken);
        return new InstantiatePrefabResult(payload);
    }

    public async Task<GetAssetInfoResult> GetAssetInfoAsync(GetAssetInfoRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetAssetInfo);
        var parameters = new JsonObject
        {
            ["asset_path"] = request.AssetPath,
        };
        var payload = await ExecuteSyncToolAsync(ToolNames.GetAssetInfo, parameters, timeoutMs, cancellationToken);
        return new GetAssetInfoResult(payload);
    }

    public async Task<FindPrefabGameObjectsResult> FindPrefabGameObjectsAsync(FindPrefabGameObjectsRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.FindPrefabGameObjects);
        var parameters = new JsonObject
        {
            ["prefab_path"] = request.PrefabPath,
        };
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            parameters["name"] = request.Name;
        }

        if (!string.IsNullOrWhiteSpace(request.Tag))
        {
            parameters["tag"] = request.Tag;
        }

        if (!string.IsNullOrWhiteSpace(request.ComponentType))
        {
            parameters["component_type"] = request.ComponentType;
        }

        if (!string.IsNullOrWhiteSpace(request.RootPath))
        {
            parameters["root_path"] = request.RootPath;
        }

        if (request.Layer.HasValue)
        {
            parameters["layer"] = request.Layer.Value;
        }

        if (request.Active.HasValue)
        {
            parameters["active"] = request.Active.Value;
        }

        parameters["max_results"] = request.MaxResults;
        parameters["offset"] = request.Offset;
        var payload = await ExecuteSyncToolAsync(ToolNames.FindPrefabGameObjects, parameters, timeoutMs, cancellationToken);
        return new FindPrefabGameObjectsResult(payload);
    }

    public async Task<ManageAssetResult> ManageAssetAsync(ManageAssetRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ManageAsset);
        var parameters = new JsonObject
        {
            ["action"] = request.Action,
            ["asset_path"] = request.AssetPath,
        };
        if (!string.IsNullOrWhiteSpace(request.AssetType))
        {
            parameters["asset_type"] = request.AssetType;
        }

        if (request.Properties is not null)
        {
            parameters["properties"] = request.Properties.DeepClone();
        }

        if (request.Overwrite)
        {
            parameters["overwrite"] = true;
        }

        if (!string.IsNullOrWhiteSpace(request.ShaderName))
        {
            parameters["shader_name"] = request.ShaderName;
        }

        if (request.Keywords is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var kw in request.Keywords)
            {
                arr.Add(kw);
            }

            parameters["keywords"] = arr;
        }

        if (!string.IsNullOrWhiteSpace(request.KeywordsAction))
        {
            parameters["keywords_action"] = request.KeywordsAction;
        }

        var payload = await ExecuteSyncToolAsync(ToolNames.ManageAsset, parameters, timeoutMs, cancellationToken);
        return new ManageAssetResult(payload);
    }

    public async Task<ManageAsmdefResult> ManageAsmdefAsync(ManageAsmdefRequest request, CancellationToken cancellationToken)
    {
        var isMutation = request.Action is not (ManageAsmdefActions.List or ManageAsmdefActions.Get);

        await EnsureEditorReadyAsync(cancellationToken);

        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ManageAsmdef);
        var parameters = new JsonObject
        {
            ["action"] = request.Action,
        };

        if (!string.IsNullOrWhiteSpace(request.Name))
            parameters["name"] = request.Name;
        if (!string.IsNullOrWhiteSpace(request.Guid))
            parameters["guid"] = request.Guid;
        if (!string.IsNullOrWhiteSpace(request.Directory))
            parameters["directory"] = request.Directory;
        if (request.RootNamespace is not null)
            parameters["root_namespace"] = request.RootNamespace;
        if (request.References is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var r in request.References) arr.Add(r);
            parameters["references"] = arr;
        }
        if (request.UseGuids.HasValue)
            parameters["use_guids"] = request.UseGuids.Value;
        if (request.IncludePlatforms is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var p in request.IncludePlatforms) arr.Add(p);
            parameters["include_platforms"] = arr;
        }
        if (request.ExcludePlatforms is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var p in request.ExcludePlatforms) arr.Add(p);
            parameters["exclude_platforms"] = arr;
        }
        if (request.AllowUnsafeCode.HasValue)
            parameters["allow_unsafe_code"] = request.AllowUnsafeCode.Value;
        if (request.AutoReferenced.HasValue)
            parameters["auto_referenced"] = request.AutoReferenced.Value;
        if (request.DefineConstraints is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var d in request.DefineConstraints) arr.Add(d);
            parameters["define_constraints"] = arr;
        }
        if (request.NoEngineReferences.HasValue)
            parameters["no_engine_references"] = request.NoEngineReferences.Value;
        if (!string.IsNullOrWhiteSpace(request.Reference))
            parameters["reference"] = request.Reference;
        if (!string.IsNullOrWhiteSpace(request.ReferenceGuid))
            parameters["reference_guid"] = request.ReferenceGuid;
        if (!string.IsNullOrWhiteSpace(request.NamePattern))
            parameters["name_pattern"] = request.NamePattern;
        if (request.MaxResults != ManageAsmdefLimits.MaxResultsDefault)
            parameters["max_results"] = request.MaxResults;
        if (request.Offset > 0)
            parameters["offset"] = request.Offset;

        // list/get は single-shot（ロックなし）
        if (!isMutation)
        {
            var readPayload = await ExecuteSyncToolAsync(ToolNames.ManageAsmdef, parameters, timeoutMs, cancellationToken);
            return new ManageAsmdefResult(readPayload);
        }

        // mutation（create/update/delete/add_reference/remove_reference）は workflow ロック
        await _workflowLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureEditorReadyAsync(cancellationToken);

            JsonNode payload;
            try
            {
                payload = await ExecuteSyncToolAsync(ToolNames.ManageAsmdef, parameters, timeoutMs, cancellationToken);
            }
            catch (McpException ex) when (ex.Code == ErrorCodes.ReconnectTimeout)
            {
                await EnsureEditorReadyAsync(cancellationToken);
                payload = new JsonObject { ["action"] = request.Action };
            }

            await WaitForCompilationAsync(payload, cancellationToken);
            return new ManageAsmdefResult(payload);
        }
        finally
        {
            _workflowLock.Release();
        }
    }

    public async Task<ManagePrefabResult> ManagePrefabAsync(ManagePrefabRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ManagePrefab);
        var parameters = new JsonObject
        {
            ["action"] = request.Action,
        };

        if (!string.IsNullOrWhiteSpace(request.GameObjectPath))
        {
            parameters["game_object_path"] = request.GameObjectPath;
        }

        if (!string.IsNullOrWhiteSpace(request.PrefabPath))
        {
            parameters["prefab_path"] = request.PrefabPath;
        }

        if (request.Connect.HasValue)
        {
            parameters["connect"] = request.Connect.Value;
        }

        if (request.Completely.HasValue)
        {
            parameters["completely"] = request.Completely.Value;
        }

        var payload = await ExecuteSyncToolAsync(ToolNames.ManagePrefab, parameters, timeoutMs, cancellationToken);
        return new ManagePrefabResult(payload);
    }

    public async Task<ManageBuildResult> ManageBuildAsync(ManageBuildRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ManageBuild);
        var parameters = BuildManageBuildParameters(request);

        var isReadOnly = request.Action is ManageBuildActions.GetPlatform
            or ManageBuildActions.GetSettings or ManageBuildActions.GetScenes
            or ManageBuildActions.ListProfiles or ManageBuildActions.GetActiveProfile
            or ManageBuildActions.BuildReport or ManageBuildActions.Validate;

        // read-only アクションは single-shot（ロックなし）
        if (isReadOnly)
        {
            var readPayload = await ExecuteSyncToolAsync(ToolNames.ManageBuild, parameters, timeoutMs, cancellationToken);
            return new ManageBuildResult(readPayload);
        }

        await _workflowLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureEditorReadyAsync(cancellationToken);

            if (request.Action == ManageBuildActions.Build)
            {
                await EnsureEditModeAsync(cancellationToken);
                await ExecuteSyncToolAsync(ToolNames.ManageBuild, parameters, timeoutMs, cancellationToken);

                var editorStateTimeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetEditorState);
                var buildDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
                while (DateTimeOffset.UtcNow < buildDeadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(2000, cancellationToken);

                    var pollPayload = await ExecuteSyncToolAsync(
                        ToolNames.ManageBuild, new JsonObject(), editorStateTimeoutMs, cancellationToken);

                    if (pollPayload is JsonObject pollObj && pollObj.ContainsKey("result"))
                    {
                        return new ManageBuildResult(pollPayload);
                    }
                }

                throw new McpException(ErrorCodes.RequestTimeout, "Build execution timed out");
            }

            if (request.Action == ManageBuildActions.SwitchPlatform)
            {
                await EnsureEditModeAsync(cancellationToken);

                try
                {
                    var switchPayload = await ExecuteSyncToolAsync(ToolNames.ManageBuild, parameters, timeoutMs, cancellationToken);
                    return new ManageBuildResult(switchPayload);
                }
                catch (McpException ex) when (ex.Code == ErrorCodes.ReconnectTimeout)
                {
                    // プラットフォーム切替によるドメインリロード。想定内。
                }

                await EnsureEditorReadyAsync(cancellationToken);

                var confirmParams = new JsonObject { ["action"] = ManageBuildActions.GetPlatform };
                var confirmPayload = await ExecuteSyncToolAsync(
                    ToolNames.ManageBuild, confirmParams,
                    ToolCatalog.DefaultTimeoutMs(ToolNames.GetEditorState), cancellationToken);
                return new ManageBuildResult(confirmPayload);
            }

            // set_settings / set_scenes / set_active_profile: コンパイルを伴う可能性がある
            await EnsureEditModeAsync(cancellationToken);

            JsonNode mutPayload;
            try
            {
                mutPayload = await ExecuteSyncToolAsync(ToolNames.ManageBuild, parameters, timeoutMs, cancellationToken);
            }
            catch (McpException ex) when (ex.Code == ErrorCodes.ReconnectTimeout)
            {
                await EnsureEditorReadyAsync(cancellationToken);
                mutPayload = new JsonObject { ["action"] = request.Action };
            }

            await WaitForCompilationAsync(mutPayload, cancellationToken);
            return new ManageBuildResult(mutPayload);
        }
        finally
        {
            _workflowLock.Release();
        }
    }

    private static JsonObject BuildManageBuildParameters(ManageBuildRequest request)
    {
        var parameters = new JsonObject { ["action"] = request.Action };

        if (!string.IsNullOrWhiteSpace(request.Target))
            parameters["target"] = request.Target;
        if (!string.IsNullOrWhiteSpace(request.OutputPath))
            parameters["output_path"] = request.OutputPath;
        if (request.Scenes is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var s in request.Scenes) arr.Add(s);
            parameters["scenes"] = arr;
        }

        if (request.Development.HasValue)
            parameters["development"] = request.Development.Value;
        if (request.Options is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var o in request.Options) arr.Add(o);
            parameters["options"] = arr;
        }

        if (!string.IsNullOrWhiteSpace(request.Subtarget))
            parameters["subtarget"] = request.Subtarget;
        if (!string.IsNullOrWhiteSpace(request.Property))
            parameters["property"] = request.Property;
        if (request.Value != null)
            parameters["value"] = request.Value;
        if (!string.IsNullOrWhiteSpace(request.DefinesAction))
            parameters["defines_action"] = request.DefinesAction;
        if (request.BuildScenes != null)
            parameters["build_scenes"] = request.BuildScenes.DeepClone();
        if (!string.IsNullOrWhiteSpace(request.ProfilePath))
            parameters["profile_path"] = request.ProfilePath;

        return parameters;
    }

    public async Task<ManagePlayerPrefsResult> ManagePlayerPrefsAsync(ManagePlayerPrefsRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ManagePlayerPrefs);
        var parameters = new JsonObject
        {
            ["action"] = request.Action,
        };

        if (request.Key is not null)
        {
            parameters["key"] = request.Key;
        }

        if (request.ValueType is not null)
        {
            parameters["value_type"] = request.ValueType;

            switch (request.ValueType)
            {
                case ManagePlayerPrefsValueTypes.String:
                    if (request.StringValue is not null)
                        parameters["value"] = request.StringValue;
                    break;
                case ManagePlayerPrefsValueTypes.Int:
                    if (request.IntValue is not null)
                        parameters["value"] = request.IntValue.Value;
                    break;
                case ManagePlayerPrefsValueTypes.Float:
                    if (request.FloatValue is not null)
                        parameters["value"] = request.FloatValue.Value;
                    break;
            }
        }

        var payload = await ExecuteSyncToolAsync(ToolNames.ManagePlayerPrefs, parameters, timeoutMs, cancellationToken);
        return new ManagePlayerPrefsResult(payload);
    }

    /// <summary>
    /// Plugin が compiling=false を返したケースでのフォールバック確認。
    /// 500ms 後に get_editor_state を1回確認し、ドメインリロードやコンパイル開始を検知する。
    /// </summary>
    private async Task CheckForDelayedCompilationAsync(JsonNode payload, CancellationToken token)
    {
        await Task.Delay(500, token);

        var editorStateTimeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetEditorState);
        string? editorState;
        try
        {
            var statePayload = await ExecuteSyncToolAsync(
                ToolNames.GetEditorState, new JsonObject(), editorStateTimeoutMs, token);
            editorState = statePayload is JsonObject stateObj
                ? JsonHelpers.GetString(stateObj, "editor_state")
                : null;
        }
        catch (McpException ex) when (ex.Code is ErrorCodes.ReconnectTimeout
            or ErrorCodes.UnityDisconnected or ErrorCodes.RequestTimeout)
        {
            // ドメインリロード検知。再接続待ち → コンパイル完了待ち。
            await EnsureEditorReadyAsync(token);
            await WaitForCompilationAsync(payload, token);
            return;
        }

        if (editorState is "compiling" or "reloading")
        {
            await WaitForCompilationAsync(payload, token);
        }
    }

    /// <summary>
    /// get_editor_state をポーリングし、コンパイル/リロード完了を待機する。
    /// 完了後にエラーがあれば payload に追加する。
    /// workflow ロック内から呼び出す前提。
    /// </summary>
    private async Task WaitForCompilationAsync(JsonNode payload, CancellationToken token)
    {
        var editorStateTimeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetEditorState);
        var compileDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(Constants.CompileGraceTimeoutMs);

        while (DateTimeOffset.UtcNow < compileDeadline)
        {
            token.ThrowIfCancellationRequested();

            string? editorState;
            try
            {
                var statePayload = await ExecuteSyncToolAsync(
                    ToolNames.GetEditorState, new JsonObject(), editorStateTimeoutMs, token);
                editorState = statePayload is JsonObject stateObj
                    ? JsonHelpers.GetString(stateObj, "editor_state")
                    : null;
            }
            catch (McpException ex) when (ex.Code is ErrorCodes.ReconnectTimeout
                or ErrorCodes.UnityDisconnected or ErrorCodes.RequestTimeout)
            {
                await EnsureEditorReadyAsync(token);
                break;
            }

            _logger.ZLogDebug($"WaitForCompilation: poll result editor_state={editorState}");

            if (editorState is not ("compiling" or "reloading"))
            {
                break;
            }

            await Task.Delay(1000, token);
        }

        await AppendErrorsIfAny(payload, token);
    }

    /// <summary>
    /// コンソールにエラーがあれば payload に errors フィールドを追加する。
    /// コンパイルエラー、アセットインポートエラー等を含む。
    /// EnsureEditorReadyAsync 済みであることを前提とする。
    /// </summary>
    private async Task AppendErrorsIfAny(JsonNode payload, CancellationToken token)
    {
        try
        {
            var consoleTimeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ReadConsole);
            var consoleParams = new JsonObject
            {
                ["max_entries"] = 50,
                ["log_type"] = new JsonArray("error"),
            };
            var consolePayload = await ExecuteSyncToolAsync(ToolNames.ReadConsole, consoleParams, consoleTimeoutMs, token);

            if (consolePayload is JsonObject consoleObj &&
                consoleObj.TryGetPropertyValue("entries", out var entriesNode) &&
                entriesNode is JsonArray entries &&
                entries.Count > 0)
            {
                if (payload is JsonObject payloadObj)
                {
                    payloadObj["errors"] = entries.DeepClone();
                }
            }
        }
        catch
        {
            // コンソール読み取り失敗は無視（本体の操作は成功している）
        }
    }

    /// <summary>
    /// コンパイルエラーがあれば McpException をスローする。
    /// Play Mode 開始前など、コンパイルが通っていることが前提条件となる操作の事前チェックに使う。
    /// EnsureEditorReadyAsync 済みであることを前提とする。
    /// </summary>
    private async Task ThrowIfCompileErrorsAsync(CancellationToken token)
    {
        var consoleTimeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.ReadConsole);
        var consoleParams = new JsonObject
        {
            ["max_entries"] = 50,
            ["log_type"] = new JsonArray("error"),
        };
        var consolePayload = await ExecuteSyncToolAsync(ToolNames.ReadConsole, consoleParams, consoleTimeoutMs, token);

        if (consolePayload is JsonObject consoleObj &&
            consoleObj.TryGetPropertyValue("entries", out var entriesNode) &&
            entriesNode is JsonArray entries &&
            entries.Count > 0)
        {
            throw new McpException(
                ErrorCodes.CompileErrors,
                "Cannot enter play mode: compilation errors exist",
                new JsonObject { ["errors"] = entries.DeepClone() });
        }
    }

    public async Task<CaptureScreenshotResult> CaptureScreenshotAsync(CaptureScreenshotRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.CaptureScreenshot);
        var parameters = new JsonObject
        {
            ["source"] = request.Source,
        };
        if (!string.IsNullOrWhiteSpace(request.CameraPath))
        {
            parameters["camera_path"] = request.CameraPath;
        }

        if (!string.IsNullOrWhiteSpace(request.OutputPath))
        {
            parameters["output_path"] = request.OutputPath;
        }

        var payload = await ExecuteSyncToolAsync(ToolNames.CaptureScreenshot, parameters, timeoutMs, cancellationToken);
        return new CaptureScreenshotResult(payload);
    }

    public async Task<RunTestsResult> RunTestsAsync(RunTestsRequest request, CancellationToken cancellationToken)
    {
        // App Nap による遅延を防ぐため、操作前に Editor を前面に出す
        await using var focusScope = await EditorFocusScope.ActivateAsync(
            _runtimeState.GetEditorPid());

        await EnsureEditorReadyAsync(cancellationToken);
        await _workflowLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureEditorReadyAsync(cancellationToken);

            // アセットをリフレッシュし、コンパイル完了を待機する。
            await EnsureEditModeAsync(cancellationToken);

            var refreshTimeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.RefreshAssets);
            JsonNode refreshPayload;
            try
            {
                refreshPayload = await ExecuteSyncToolAsync(
                    ToolNames.RefreshAssets, new JsonObject(), refreshTimeoutMs, cancellationToken);
            }
            catch (McpException ex) when (ex.Code == ErrorCodes.ReconnectTimeout)
            {
                await EnsureEditorReadyAsync(cancellationToken);
                refreshPayload = new JsonObject { ["refreshed"] = true, ["compiling"] = true };
            }

            {
                var compiling = refreshPayload is JsonObject rpo
                    && (JsonHelpers.GetBool(rpo, "compiling") ?? false);
                var compilationFailed = refreshPayload is JsonObject rpf
                    && (JsonHelpers.GetBool(rpf, "compilation_failed") ?? false);
                if (refreshPayload is JsonObject robj)
                {
                    robj.Remove("compiling");
                    robj.Remove("compilation_failed");
                }

                if (compiling)
                {
                    await WaitForCompilationAsync(refreshPayload, cancellationToken);
                }
                else if (compilationFailed)
                {
                    await AppendErrorsIfAny(refreshPayload, cancellationToken);
                }
                else
                {
                    await CheckForDelayedCompilationAsync(refreshPayload, cancellationToken);
                }
            }

            // コンパイルエラーがあればテスト実行をスキップしてエラーを返す
            if (refreshPayload is JsonObject refreshObj
                && refreshObj.TryGetPropertyValue("errors", out var errorsNode)
                && errorsNode is JsonArray errors
                && errors.Count > 0)
            {
                var result = BuildEmptyRunTestsPayload(request);
                result["errors"] = errors.DeepClone();
                return new RunTestsResult(result);
            }

            // テスト開始（Plugin は即座に応答し、バックグラウンドでテストを実行する）
            var timeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.RunTests);
            var parameters = BuildRunTestsParameters(request);
            await ExecuteSyncToolAsync(ToolNames.RunTests, parameters, timeoutMs, cancellationToken);

            // テスト完了をポーリングで待機。
            var editorStateTimeoutMs = ToolCatalog.DefaultTimeoutMs(ToolNames.GetEditorState);
            var testDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
            while (DateTimeOffset.UtcNow < testDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1000, cancellationToken);

                var pollPayload = await ExecuteSyncToolAsync(
                    ToolNames.RunTests, new JsonObject(), editorStateTimeoutMs, cancellationToken);

                if (pollPayload is JsonObject pollObj && pollObj.ContainsKey("summary"))
                {
                    return new RunTestsResult(pollPayload);
                }

                _logger.ZLogDebug($"Waiting for test execution to complete");
            }

            throw new McpException(ErrorCodes.RequestTimeout, "Test execution timed out");
        }
        finally
        {
            _workflowLock.Release();
        }
    }

    private static JsonObject BuildEmptyRunTestsPayload(RunTestsRequest request)
    {
        return new JsonObject
        {
            ["summary"] = new JsonObject
            {
                ["total"] = 0,
                ["passed"] = 0,
                ["failed"] = 0,
                ["skipped"] = 0,
                ["duration_ms"] = 0,
            },
            ["failed_tests"] = new JsonArray(),
            ["mode"] = request.Mode ?? "all",
            ["test_full_name"] = request.TestFullName ?? "",
            ["test_name_pattern"] = request.TestNamePattern ?? "",
        };
    }

    private static JsonObject BuildRunTestsParameters(RunTestsRequest request)
    {
        var parameters = new JsonObject
        {
            ["mode"] = request.Mode,
        };
        if (!string.IsNullOrWhiteSpace(request.TestFullName))
        {
            parameters["test_full_name"] = request.TestFullName;
        }

        if (!string.IsNullOrWhiteSpace(request.TestNamePattern))
        {
            parameters["test_name_pattern"] = request.TestNamePattern;
        }

        return parameters;
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
                _logger.ZLogWarning($"Unity websocket receive error error={ex.Message}");
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
            _logger.ZLogWarning($"Received non-JSON message from Unity");
            return;
        }

        var type = JsonHelpers.GetString(message, "type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        if (!string.Equals(type, "hello", StringComparison.Ordinal) && !_sessionRegistry.IsActive(sourceSocket))
        {
            _logger.ZLogWarning($"Received message from non-active Unity websocket type={type}");
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

        _logger.ZLogWarning($"Unhandled message from Unity type={type} request_id={requestId}");
    }

    private async Task HandleHelloAsync(WebSocket socket, JsonObject message, CancellationToken cancellationToken)
    {
        var protocolVersion = JsonHelpers.GetInt(message, "protocol_version");
        if (protocolVersion.HasValue && protocolVersion.Value != Constants.ProtocolVersion)
        {
            await SendRawAsync(socket, new JsonObject
            {
                ["type"] = "error",
                ["protocol_version"] = Constants.ProtocolVersion,
                ["request_id"] = JsonHelpers.CloneNode(message["request_id"]),
                ["error"] = new JsonObject
                {
                    ["code"] = ErrorCodes.InvalidRequest,
                    ["message"] = "protocol_version mismatch",
                },
            }, cancellationToken);

            await SafeCloseSocketAsync(socket, WebSocketCloseStatus.ProtocolError, "protocol-version-mismatch", cancellationToken);
            return;
        }

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
            ["tools"] = ToolCatalog.BuildUnityCapabilityTools(),
        }, cancellationToken);

        RecordMessageReceived();
        _runtimeState.OnConnected(initialState, connectionId, editorInstanceId);
        StartStaleTimer(socket, connectionId, editorInstanceId);

        var isInitial = Interlocked.Exchange(ref _hasEverConnected, 1) == 0;
        var connectLogLevel = isInitial ? LogLevel.Information : LogLevel.Debug;
        if (_logger.IsEnabled(connectLogLevel))
        {
            var (shouldLog, suppressed) = _sessionConnectLog.Check();
            if (shouldLog)
            {
                if (isInitial)
                {
                    // editor_instance_id は "{pid}:{project_path}/Assets" 形式。プロジェクトパスのみ抽出。
                    var projectPath = editorInstanceId ?? "";
                    var colonIdx = projectPath.IndexOf(':');
                    if (colonIdx >= 0) projectPath = projectPath[(colonIdx + 1)..];
                    if (projectPath.EndsWith("/Assets", StringComparison.Ordinal)) projectPath = projectPath[..^"/Assets".Length];
                    _logger.ZLogInformation($"Unity connected {projectPath}");
                }
                else
                {
                    var sup = suppressed > 0 ? suppressed : (int?)null;
                    _logger.ZLogDebug($"Unity websocket session activated (reconnect) connection_id={connectionId} editor_instance_id={editorInstanceId} plugin_session_id={pluginSessionId} connect_attempt_seq={connectAttemptSeq} editor_state={initialState.ToWire()} suppressed={sup}");
                }
            }
        }
    }

    private async Task ReplaceActiveSessionAsync(WebSocket replacedSocket)
    {
        var snapshot = _runtimeState.GetSnapshot();
        StopStaleTimer();
        FailPendingRequestsAsDisconnected();
        _runtimeState.OnDisconnected();
        await SafeCloseSocketAsync(replacedSocket, WebSocketCloseStatus.NormalClosure, "replaced-by-same-editor", CancellationToken.None);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var (shouldLog, suppressed) = _sessionDisconnectLog.Check();
            if (shouldLog)
            {
                var sup = suppressed > 0 ? suppressed : (int?)null;
                _logger.ZLogDebug($"Unity websocket session replaced existing active session for same editor replaced_connection_id={snapshot.ActiveConnectionId} editor_instance_id={snapshot.EditorInstanceId} suppressed={sup}");
            }
        }
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
                        _logger.ZLogWarning($"Stale connection timeout reached. Closing Unity websocket. connection_id={connectionId} editor_instance_id={editorInstanceId} timeout_ms={Constants.StaleConnectionTimeoutMs}");
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
                _logger.ZLogWarning($"Stale timer loop error error={ex.Message}");
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
            // .WaitAsync(cancellationToken) を使わない。
            // timeoutCts が cancellationToken にリンクされているため、
            // 外部キャンセル時は Register コールバック経由で McpException(RequestTimeout) が設定される。
            // WebSocket 切断時は FailPendingRequestsAsDisconnected が McpException(UnityDisconnected) を設定する。
            // いずれも McpException として catch ブロックに到達し、NormalizeDispatchFailure で正規化される。
            // .WaitAsync を使うと TaskCanceledException が発生し、
            // ReconnectTimeout catch によるリカバリパスをバイパスしてしまう。
            var response = await completion.Task;
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
        if (snapshot.WaitingReason is "compiling" or "reloading" or "entering_play_mode" or "exiting_play_mode")
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
        if (wasConnected && !IsShuttingDown() && _logger.IsEnabled(LogLevel.Debug))
        {
            var (shouldLog, suppressed) = _sessionDisconnectLog.Check();
            if (shouldLog)
            {
                var sup = suppressed > 0 ? suppressed : (int?)null;
                _logger.ZLogDebug($"Unity websocket session disconnected connection_id={snapshot.ActiveConnectionId} editor_instance_id={snapshot.EditorInstanceId} suppressed={sup}");
            }
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

    private void LogRejectedActiveSessionWarning(string? editorInstanceId, string? pluginSessionId, ulong? connectAttemptSeq)
    {
        var (shouldEmit, suppressed) = _sessionRejectLog.Check();
        if (!shouldEmit)
        {
            return;
        }

        var sup = suppressed > 0 ? suppressed : (int?)null;
        var throttleSec = suppressed > 0 ? (int?)LifecycleLogThrottleInterval.TotalSeconds : null;
        _logger.ZLogWarning($"Rejected Unity websocket session because another editor is active editor_instance_id={editorInstanceId} plugin_session_id={pluginSessionId} connect_attempt_seq={connectAttemptSeq} suppressed={sup} throttle_sec={throttleSec}");
    }

    private async Task SendRawAsync(WebSocket socket, JsonObject payload, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                throw new McpException(ErrorCodes.UnityDisconnected, "Unity websocket is not connected");
            }

            var json = payload.ToJsonString(JsonDefaults.Options);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
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
