#nullable enable

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace UnityMcpServer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ServerConfig config;
        try
        {
            config = ConfigLoader.Parse(args);
        }
        catch (UnityMcpException ex) when (ex.Code == ErrorCodes.ConfigValidation)
        {
            Logger.Error(
                "Configuration validation failed",
                ("code", ex.Code),
                ("message", ex.Message),
                ("details", ex.Details));
            return 1;
        }

        try
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, config.Port);
            });

            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            });

            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton<RuntimeState>();
            builder.Services.AddSingleton(_ => new RequestScheduler(Constants.QueueMaxSize));
            builder.Services.AddSingleton<UnityBridge>();
            builder.Services.AddSingleton<McpToolService>();
            builder.Services.AddSingleton<McpHttpHandler>();

            var app = builder.Build();
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });

            var runtimeState = app.Services.GetRequiredService<RuntimeState>();
            runtimeState.SetServerState(ServerState.WaitingEditor);

            app.MapMethods(Constants.McpHttpPath, new[] { HttpMethods.Post }, static (HttpContext context, McpHttpHandler handler) =>
                handler.HandlePostAsync(context));

            app.MapMethods(Constants.McpHttpPath, new[] { HttpMethods.Get }, static (HttpContext context, McpHttpHandler handler) =>
                handler.HandleGetAsync(context));

            app.MapMethods(Constants.McpHttpPath, new[] { HttpMethods.Delete }, static (HttpContext context, McpHttpHandler handler) =>
                handler.HandleDeleteAsync(context));

            app.Map(Constants.UnityWsPath, static (HttpContext context, UnityBridge bridge) =>
                bridge.HandleWebSocketEndpointAsync(context));

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                Logger.Info(
                    "Unity MCP server started",
                    ("host", Constants.Host),
                    ("port", config.Port),
                    ("mcp_path", Constants.McpHttpPath),
                    ("unity_ws_path", Constants.UnityWsPath),
                    ("server_state", runtimeState.GetSnapshot().ServerState));
            });

            app.Lifetime.ApplicationStopping.Register(() =>
            {
                runtimeState.SetServerState(ServerState.Stopping);
                Logger.Info("Server stopping");
            });

            app.Lifetime.ApplicationStopped.Register(() =>
            {
                runtimeState.SetServerState(ServerState.Stopped);
                Logger.Info("Server stopped");
            });

            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("Server crashed", ("error", ex.Message));
            return 1;
        }
    }
}

internal sealed record ServerConfig(int Port);

internal static class Constants
{
    public const string ServerName = "unity-mcp";
    public const string ServerVersion = "0.1.0";
    public const string Host = "127.0.0.1";
    public const int DefaultPort = 48091;
    public const string McpHttpPath = "/mcp";
    public const string UnityWsPath = "/unity";
    public const int ProtocolVersion = 1;
    public const int QueueMaxSize = 32;
    public const int MaxMessageBytes = 1024 * 1024;
    public const int HeartbeatIntervalMs = 3000;
    public const int HeartbeatTimeoutMs = 4500;
    public const int RequestReconnectWaitMs = 2500;
    public const string McpSessionHeader = "Mcp-Session-Id";
    public const string DefaultMcpProtocolVersion = "2025-03-26";
}

internal static class ErrorCodes
{
    public const string ConfigValidation = "ERR_CONFIG_VALIDATION";
    public const string InvalidRequest = "ERR_INVALID_REQUEST";
    public const string InvalidParams = "ERR_INVALID_PARAMS";
    public const string UnknownCommand = "ERR_UNKNOWN_COMMAND";
    public const string EditorNotReady = "ERR_EDITOR_NOT_READY";
    public const string UnityDisconnected = "ERR_UNITY_DISCONNECTED";
    public const string ReconnectTimeout = "ERR_RECONNECT_TIMEOUT";
    public const string RequestTimeout = "ERR_REQUEST_TIMEOUT";
    public const string QueueFull = "ERR_QUEUE_FULL";
    public const string JobNotFound = "ERR_JOB_NOT_FOUND";
    public const string UnityExecution = "ERR_UNITY_EXECUTION";
    public const string InvalidResponse = "ERR_INVALID_RESPONSE";
    public const string CancelRejected = "ERR_CANCEL_REJECTED";
}

internal static class ConfigLoader
{
    public static ServerConfig Parse(string[] args)
    {
        var port = Constants.DefaultPort;

        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            if (string.Equals(current, "--port", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    throw new UnityMcpException(ErrorCodes.ConfigValidation, "--port requires a value");
                }

                if (!int.TryParse(args[i + 1], out port))
                {
                    throw new UnityMcpException(
                        ErrorCodes.ConfigValidation,
                        "--port must be an integer",
                        new JsonObject { ["port"] = args[i + 1] });
                }

                i += 1;
                continue;
            }

            if (current.StartsWith("--port=", StringComparison.Ordinal))
            {
                var raw = current["--port=".Length..];
                if (!int.TryParse(raw, out port))
                {
                    throw new UnityMcpException(
                        ErrorCodes.ConfigValidation,
                        "--port must be an integer",
                        new JsonObject { ["port"] = raw });
                }
            }
        }

        if (port is < 1 or > 65535)
        {
            throw new UnityMcpException(
                ErrorCodes.ConfigValidation,
                "--port must be between 1 and 65535",
                new JsonObject { ["port"] = port });
        }

        return new ServerConfig(port);
    }
}

internal sealed class UnityMcpException : Exception
{
    public UnityMcpException(string code, string message, JsonNode? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public JsonNode? Details { get; }
}

internal static class Logger
{
    private static readonly object Gate = new();

    public static void Info(string message, params (string Key, object? Value)[] context) => Write("INFO", message, context);

    public static void Warn(string message, params (string Key, object? Value)[] context) => Write("WARN", message, context);

    public static void Error(string message, params (string Key, object? Value)[] context) => Write("ERROR", message, context);

    private static void Write(string level, string message, IReadOnlyList<(string Key, object? Value)> context)
    {
        var payload = new JsonObject
        {
            ["level"] = level,
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["msg"] = message,
        };

        foreach (var (key, value) in context)
        {
            payload[key] = ToJsonNode(value);
        }

        lock (Gate)
        {
            Console.WriteLine(payload.ToJsonString(JsonDefaults.Options));
        }
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            Exception ex => new JsonObject
            {
                ["type"] = ex.GetType().Name,
                ["message"] = ex.Message,
            },
            _ => JsonSerializer.SerializeToNode(value, JsonDefaults.Options),
        };
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}

internal enum ServerState
{
    Booting,
    WaitingEditor,
    Ready,
    Stopping,
    Stopped,
}

internal enum EditorState
{
    Unknown,
    Ready,
    Compiling,
    Reloading,
}

internal enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Timeout,
    Cancelled,
}

internal static class WireState
{
    public static string ToWire(this ServerState state) => state switch
    {
        ServerState.Booting => "booting",
        ServerState.WaitingEditor => "waiting_editor",
        ServerState.Ready => "ready",
        ServerState.Stopping => "stopping",
        ServerState.Stopped => "stopped",
        _ => "stopped",
    };

    public static string ToWire(this EditorState state) => state switch
    {
        EditorState.Unknown => "unknown",
        EditorState.Ready => "ready",
        EditorState.Compiling => "compiling",
        EditorState.Reloading => "reloading",
        _ => "unknown",
    };

    public static string ToWire(this JobState state) => state switch
    {
        JobState.Queued => "queued",
        JobState.Running => "running",
        JobState.Succeeded => "succeeded",
        JobState.Failed => "failed",
        JobState.Timeout => "timeout",
        JobState.Cancelled => "cancelled",
        _ => "failed",
    };

    public static EditorState ParseEditorState(string? value) => value switch
    {
        "ready" => EditorState.Ready,
        "compiling" => EditorState.Compiling,
        "reloading" => EditorState.Reloading,
        _ => EditorState.Unknown,
    };

    public static bool TryParseJobState(string? value, out JobState state)
    {
        state = value switch
        {
            "queued" => JobState.Queued,
            "running" => JobState.Running,
            "succeeded" => JobState.Succeeded,
            "failed" => JobState.Failed,
            "timeout" => JobState.Timeout,
            "cancelled" => JobState.Cancelled,
            _ => JobState.Failed,
        };

        return value is "queued" or "running" or "succeeded" or "failed" or "timeout" or "cancelled";
    }

    public static bool IsTerminal(JobState state) => state is JobState.Succeeded or JobState.Failed or JobState.Timeout or JobState.Cancelled;
}

internal sealed record RuntimeSnapshot(string ServerState, string EditorState, bool Connected, ulong LastEditorStatusSeq)
{
    public JsonObject ToJson() => new()
    {
        ["server_state"] = ServerState,
        ["editor_state"] = EditorState,
        ["connected"] = Connected,
        ["last_editor_status_seq"] = LastEditorStatusSeq,
    };
}

internal sealed class RuntimeState
{
    private readonly object _gate = new();
    private ServerState _serverState = ServerState.Booting;
    private EditorState _editorState = EditorState.Unknown;
    private bool _connected;
    private ulong _lastEditorStatusSeq;

    public event Action? StateChanged;

    public RuntimeSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new RuntimeSnapshot(
                _serverState.ToWire(),
                _connected ? _editorState.ToWire() : EditorState.Unknown.ToWire(),
                _connected,
                _lastEditorStatusSeq);
        }
    }

    public bool IsEditorReady()
    {
        lock (_gate)
        {
            return _connected && _editorState == EditorState.Ready;
        }
    }

    public void SetServerState(ServerState next)
    {
        var changed = false;
        lock (_gate)
        {
            if (_serverState != next)
            {
                _serverState = next;
                changed = true;
            }
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }
    }

    public void OnConnected(EditorState initialEditorState)
    {
        lock (_gate)
        {
            _connected = true;
            _editorState = initialEditorState;
            _serverState = ServerState.Ready;
        }

        StateChanged?.Invoke();
    }

    public void OnDisconnected()
    {
        lock (_gate)
        {
            _connected = false;
            _editorState = EditorState.Unknown;
            if (_serverState is not ServerState.Stopping and not ServerState.Stopped)
            {
                _serverState = ServerState.WaitingEditor;
            }
        }

        StateChanged?.Invoke();
    }

    public void OnEditorStatus(EditorState state, ulong seq)
    {
        var changed = false;
        lock (_gate)
        {
            if (seq > _lastEditorStatusSeq)
            {
                _lastEditorStatusSeq = seq;
                _editorState = state;
                changed = true;
            }
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }
    }

    public void OnPong(EditorState? state, ulong? seq)
    {
        lock (_gate)
        {
            if (state.HasValue)
            {
                _editorState = state.Value;
            }

            if (seq.HasValue && seq.Value > _lastEditorStatusSeq)
            {
                _lastEditorStatusSeq = seq.Value;
            }
        }

        StateChanged?.Invoke();
    }

    public async Task<bool> WaitForEditorReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsEditorReady())
        {
            return true;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler()
        {
            if (IsEditorReady())
            {
                tcs.TrySetResult(true);
            }
        }

        StateChanged += Handler;
        try
        {
            if (IsEditorReady())
            {
                return true;
            }

            var delayTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, delayTask);
            if (completed == tcs.Task)
            {
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            StateChanged -= Handler;
        }
    }
}

internal sealed class RequestScheduler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _maxQueueSize;
    private int _queuedCount;
    private int _runningCount;

    public RequestScheduler(int maxQueueSize)
    {
        _maxQueueSize = maxQueueSize;
    }

    public async Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var queuedNow = Interlocked.Increment(ref _queuedCount);
        var runningNow = Volatile.Read(ref _runningCount);

        if (queuedNow + runningNow > _maxQueueSize)
        {
            Interlocked.Decrement(ref _queuedCount);
            throw new UnityMcpException(ErrorCodes.QueueFull, "Queue is full");
        }

        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        catch
        {
            Interlocked.Decrement(ref _queuedCount);
            throw;
        }

        Interlocked.Decrement(ref _queuedCount);
        Interlocked.Increment(ref _runningCount);

        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _runningCount);
            _gate.Release();
        }
    }
}

internal sealed record ToolMetadata(
    string Name,
    string ExecutionMode,
    bool SupportsCancel,
    int DefaultTimeoutMs,
    int MaxTimeoutMs,
    bool RequiresClientRequestId,
    bool ExecutionErrorRetryable,
    string Description,
    JsonObject InputSchema);

internal static class ToolCatalog
{
    public static readonly IReadOnlyDictionary<string, ToolMetadata> Items = new Dictionary<string, ToolMetadata>(StringComparer.Ordinal)
    {
        ["get_editor_state"] = new(
            "get_editor_state",
            "sync",
            false,
            5000,
            10000,
            false,
            true,
            "Returns current server/editor connection state.",
            EmptyObjectSchema()),
        ["read_console"] = new(
            "read_console",
            "sync",
            false,
            10000,
            30000,
            false,
            true,
            "Reads Unity console entries.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["max_entries"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 1,
                        ["maximum"] = 2000,
                        ["default"] = 200,
                    },
                },
                ["additionalProperties"] = false,
            }),
        ["run_tests"] = new(
            "run_tests",
            "job",
            true,
            300000,
            1800000,
            false,
            false,
            "Starts Unity tests as a cancellable job.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["mode"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("all", "edit", "play"),
                        ["default"] = "all",
                    },
                    ["filter"] = new JsonObject
                    {
                        ["type"] = "string",
                    },
                },
                ["additionalProperties"] = false,
            }),
        ["get_job_status"] = new(
            "get_job_status",
            "sync",
            false,
            5000,
            10000,
            false,
            false,
            "Checks state/result of a submitted test job.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["job_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["minLength"] = 1,
                    },
                },
                ["required"] = new JsonArray("job_id"),
                ["additionalProperties"] = false,
            }),
        ["cancel_job"] = new(
            "cancel_job",
            "sync",
            false,
            5000,
            10000,
            false,
            false,
            "Requests cancellation for a running/queued job.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["job_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["minLength"] = 1,
                    },
                },
                ["required"] = new JsonArray("job_id"),
                ["additionalProperties"] = false,
            }),
    };

    public static JsonArray BuildMcpTools()
    {
        var tools = new JsonArray();
        foreach (var tool in Items.Values)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }

        return tools;
    }

    public static JsonArray BuildUnityCapabilityTools()
    {
        var tools = new JsonArray();
        foreach (var tool in Items.Values)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["execution_mode"] = tool.ExecutionMode,
                ["supports_cancel"] = tool.SupportsCancel,
                ["default_timeout_ms"] = tool.DefaultTimeoutMs,
                ["max_timeout_ms"] = tool.MaxTimeoutMs,
                ["requires_client_request_id"] = tool.RequiresClientRequestId,
                ["execution_error_retryable"] = tool.ExecutionErrorRetryable,
            });
        }

        return tools;
    }

    public static int DefaultTimeoutMs(string toolName)
    {
        if (!Items.TryGetValue(toolName, out var metadata))
        {
            throw new UnityMcpException(ErrorCodes.UnknownCommand, $"Unknown tool: {toolName}");
        }

        return metadata.DefaultTimeoutMs;
    }

    private static JsonObject EmptyObjectSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["additionalProperties"] = false,
        };
    }
}

internal sealed class UnityBridge
{
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

    private sealed record JobRecord(JobState State, JsonNode? Result);

    private readonly RuntimeState _runtimeState;
    private readonly RequestScheduler _scheduler;
    private readonly object _socketGate = new();
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new(StringComparer.Ordinal);

    private WebSocket? _socket;
    private CancellationTokenSource? _heartbeatCts;
    private DateTimeOffset _lastPongAt = DateTimeOffset.MinValue;

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

        WebSocket? previousSocket;
        lock (_socketGate)
        {
            previousSocket = _socket;
            _socket = socket;
            _lastPongAt = DateTimeOffset.UtcNow;
        }

        if (previousSocket is not null)
        {
            await SafeCloseSocketAsync(previousSocket, WebSocketCloseStatus.NormalClosure, "superseded", CancellationToken.None);
        }

        Logger.Info("Unity websocket connected", ("remote", context.Connection.RemoteIpAddress?.ToString()));

        try
        {
            await ReceiveLoopAsync(socket, context.RequestAborted);
        }
        finally
        {
            OnSocketClosed(socket);
        }
    }

    public Task<JsonObject> ReadConsoleAsync(int maxEntries, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            await EnsureEditorReadyAsync(token);

            var request = new JsonObject
            {
                ["type"] = "execute",
                ["protocol_version"] = Constants.ProtocolVersion,
                ["request_id"] = Guid.NewGuid().ToString("D"),
                ["tool_name"] = "read_console",
                ["params"] = new JsonObject
                {
                    ["max_entries"] = maxEntries,
                },
                ["timeout_ms"] = ToolCatalog.DefaultTimeoutMs("read_console"),
            };

            var response = await SendRequestAsync(
                request,
                "result",
                TimeSpan.FromMilliseconds(ToolCatalog.DefaultTimeoutMs("read_console")),
                token);

            var status = JsonHelpers.GetString(response, "status");
            if (string.Equals(status, "ok", StringComparison.Ordinal))
            {
                return JsonHelpers.AsObjectOrEmpty(response["result"]);
            }

            throw new UnityMcpException(
                ErrorCodes.UnityExecution,
                "Unity returned execution error",
                JsonHelpers.CloneNode(response));
        }, cancellationToken);
    }

    public Task<JsonObject> RunTestsAsync(string mode, string? filter, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            await EnsureEditorReadyAsync(token);

            var parameters = new JsonObject
            {
                ["mode"] = mode,
            };
            if (!string.IsNullOrWhiteSpace(filter))
            {
                parameters["filter"] = filter;
            }

            var request = new JsonObject
            {
                ["type"] = "submit_job",
                ["protocol_version"] = Constants.ProtocolVersion,
                ["request_id"] = Guid.NewGuid().ToString("D"),
                ["tool_name"] = "run_tests",
                ["params"] = parameters,
                ["timeout_ms"] = ToolCatalog.DefaultTimeoutMs("run_tests"),
            };

            var response = await SendRequestAsync(
                request,
                "submit_job_result",
                TimeSpan.FromMilliseconds(ToolCatalog.DefaultTimeoutMs("run_tests")),
                token);

            var status = JsonHelpers.GetString(response, "status");
            var jobId = JsonHelpers.GetString(response, "job_id");
            if (!string.Equals(status, "accepted", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(jobId))
            {
                throw new UnityMcpException(
                    ErrorCodes.InvalidResponse,
                    "Invalid submit_job_result payload",
                    JsonHelpers.CloneNode(response));
            }

            _jobs[jobId] = new JobRecord(JobState.Queued, null);

            return new JsonObject
            {
                ["job_id"] = jobId,
                ["state"] = "queued",
            };
        }, cancellationToken);
    }

    public Task<JsonObject> GetJobStatusAsync(string jobId, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            if (!_jobs.ContainsKey(jobId))
            {
                throw new UnityMcpException(ErrorCodes.JobNotFound, $"Unknown job_id: {jobId}");
            }

            await EnsureEditorReadyAsync(token);

            var request = new JsonObject
            {
                ["type"] = "get_job_status",
                ["protocol_version"] = Constants.ProtocolVersion,
                ["request_id"] = Guid.NewGuid().ToString("D"),
                ["job_id"] = jobId,
            };

            var response = await SendRequestAsync(
                request,
                "job_status",
                TimeSpan.FromMilliseconds(ToolCatalog.DefaultTimeoutMs("get_job_status")),
                token);

            var stateRaw = JsonHelpers.GetString(response, "state");
            if (!WireState.TryParseJobState(stateRaw, out var state))
            {
                throw new UnityMcpException(
                    ErrorCodes.InvalidResponse,
                    "Invalid job_status.state value",
                    JsonHelpers.CloneNode(response));
            }

            var result = JsonHelpers.CloneNode(response["result"]) ?? new JsonObject();
            _jobs[jobId] = new JobRecord(state, result);

            return new JsonObject
            {
                ["job_id"] = jobId,
                ["state"] = state.ToWire(),
                ["progress"] = null,
                ["result"] = result,
            };
        }, cancellationToken);
    }

    public Task<JsonObject> CancelJobAsync(string jobId, CancellationToken cancellationToken)
    {
        return _scheduler.EnqueueAsync(async token =>
        {
            if (!_jobs.TryGetValue(jobId, out var existing))
            {
                throw new UnityMcpException(ErrorCodes.JobNotFound, $"Unknown job_id: {jobId}");
            }

            if (existing.State == JobState.Queued)
            {
                _jobs[jobId] = existing with { State = JobState.Cancelled };
                return new JsonObject
                {
                    ["job_id"] = jobId,
                    ["status"] = "cancelled",
                };
            }

            if (WireState.IsTerminal(existing.State))
            {
                return new JsonObject
                {
                    ["job_id"] = jobId,
                    ["status"] = "rejected",
                };
            }

            await EnsureEditorReadyAsync(token);

            var request = new JsonObject
            {
                ["type"] = "cancel",
                ["protocol_version"] = Constants.ProtocolVersion,
                ["request_id"] = Guid.NewGuid().ToString("D"),
                ["target_job_id"] = jobId,
            };

            var response = await SendRequestAsync(
                request,
                "cancel_result",
                TimeSpan.FromMilliseconds(ToolCatalog.DefaultTimeoutMs("cancel_job")),
                token);

            var status = JsonHelpers.GetString(response, "status");
            if (status is not ("cancelled" or "cancel_requested" or "rejected"))
            {
                throw new UnityMcpException(
                    ErrorCodes.InvalidResponse,
                    "Invalid cancel_result.status value",
                    JsonHelpers.CloneNode(response));
            }

            if (string.Equals(status, "cancelled", StringComparison.Ordinal))
            {
                _jobs[jobId] = existing with { State = JobState.Cancelled };
            }

            return new JsonObject
            {
                ["job_id"] = jobId,
                ["status"] = status,
            };
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
            Logger.Warn("Unity websocket receive error", ("error", ex.Message));
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

        switch (type)
        {
            case "hello":
                await HandleHelloAsync(sourceSocket, message, cancellationToken);
                return;
            case "editor_status":
                HandleEditorStatus(message);
                return;
            case "pong":
                HandlePong(message);
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
                    pending.Completion.TrySetException(new UnityMcpException(
                        JsonHelpers.GetString(error, "code") ?? ErrorCodes.UnityExecution,
                        JsonHelpers.GetString(error, "message") ?? "Unity returned error",
                        JsonHelpers.CloneNode(error["details"])));

                    return;
                }

                if (!string.Equals(type, pending.ExpectedType, StringComparison.Ordinal))
                {
                    pending.Completion.TrySetException(new UnityMcpException(
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
        var pluginVersion = JsonHelpers.GetString(message, "plugin_version") ?? "unknown";
        var initialState = WireState.ParseEditorState(JsonHelpers.GetString(message, "state"));

        _runtimeState.OnConnected(initialState);
        StartHeartbeat(socket);

        await SendRawAsync(socket, new JsonObject
        {
            ["type"] = "hello",
            ["protocol_version"] = Constants.ProtocolVersion,
            ["server_version"] = Constants.ServerVersion,
        }, cancellationToken);

        await SendRawAsync(socket, new JsonObject
        {
            ["type"] = "capability",
            ["protocol_version"] = Constants.ProtocolVersion,
            ["tools"] = ToolCatalog.BuildUnityCapabilityTools(),
        }, cancellationToken);

        Logger.Info("Unity hello received", ("plugin_version", pluginVersion), ("editor_state", initialState.ToWire()));
    }

    private void HandleEditorStatus(JsonObject message)
    {
        var state = WireState.ParseEditorState(JsonHelpers.GetString(message, "state"));
        var seq = JsonHelpers.GetUlong(message, "seq") ?? 0;
        _runtimeState.OnEditorStatus(state, seq);
    }

    private void HandlePong(JsonObject message)
    {
        _lastPongAt = DateTimeOffset.UtcNow;

        EditorState? state = null;
        var editorStateRaw = JsonHelpers.GetString(message, "editor_state");
        if (!string.IsNullOrWhiteSpace(editorStateRaw))
        {
            state = WireState.ParseEditorState(editorStateRaw);
        }

        var seq = JsonHelpers.GetUlong(message, "seq");
        _runtimeState.OnPong(state, seq);
    }

    private async Task<JsonObject> SendRequestAsync(JsonObject message, string expectedType, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var requestId = JsonHelpers.GetString(message, "request_id");
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new UnityMcpException(ErrorCodes.InvalidRequest, "request_id is required");
        }

        var socket = GetOpenSocketOrThrow();

        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var pending = new PendingRequest(expectedType, completion, timeoutCts);
        if (!_pendingRequests.TryAdd(requestId, pending))
        {
            pending.Dispose();
            throw new UnityMcpException(ErrorCodes.InvalidRequest, $"Duplicate request_id: {requestId}");
        }

        using var timeoutRegistration = timeoutCts.Token.Register(() =>
        {
            if (_pendingRequests.TryRemove(requestId, out var timedOutPending))
            {
                timedOutPending.Completion.TrySetException(new UnityMcpException(
                    ErrorCodes.RequestTimeout,
                    $"Unity request timeout: {requestId}"));
                timedOutPending.Dispose();
            }
        });

        try
        {
            await SendRawAsync(socket, message, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
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

        var resumed = await _runtimeState.WaitForEditorReadyAsync(
            TimeSpan.FromMilliseconds(Constants.RequestReconnectWaitMs),
            cancellationToken);

        if (!resumed)
        {
            throw new UnityMcpException(ErrorCodes.EditorNotReady, "Editor is not ready");
        }
    }

    private WebSocket GetOpenSocketOrThrow()
    {
        lock (_socketGate)
        {
            if (_socket is null || _socket.State != WebSocketState.Open)
            {
                throw new UnityMcpException(ErrorCodes.UnityDisconnected, "Unity websocket is not connected");
            }

            return _socket;
        }
    }

    private void StartHeartbeat(WebSocket socket)
    {
        StopHeartbeat();
        var heartbeatCts = new CancellationTokenSource();
        _heartbeatCts = heartbeatCts;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!heartbeatCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(Constants.HeartbeatIntervalMs, heartbeatCts.Token);

                    if (socket.State != WebSocketState.Open)
                    {
                        return;
                    }

                    var pingSentAt = DateTimeOffset.UtcNow;
                    await SendRawAsync(socket, new JsonObject
                    {
                        ["type"] = "ping",
                        ["protocol_version"] = Constants.ProtocolVersion,
                    }, heartbeatCts.Token);

                    await Task.Delay(Constants.HeartbeatTimeoutMs, heartbeatCts.Token);
                    if (_lastPongAt < pingSentAt && socket.State == WebSocketState.Open)
                    {
                        Logger.Warn("Heartbeat timeout. Closing Unity websocket.");
                        await SafeCloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "heartbeat-timeout", CancellationToken.None);
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
                Logger.Warn("Heartbeat loop error", ("error", ex.Message));
            }
        });
    }

    private void StopHeartbeat()
    {
        var cts = Interlocked.Exchange(ref _heartbeatCts, null);
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

    private void OnSocketClosed(WebSocket closedSocket)
    {
        var wasCurrent = false;
        lock (_socketGate)
        {
            if (ReferenceEquals(_socket, closedSocket))
            {
                _socket = null;
                wasCurrent = true;
            }
        }

        if (!wasCurrent)
        {
            return;
        }

        StopHeartbeat();

        foreach (var (requestId, pending) in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(requestId, out var removed))
            {
                removed.Completion.TrySetException(new UnityMcpException(
                    ErrorCodes.UnityDisconnected,
                    "Unity websocket disconnected"));
                removed.Dispose();
            }
        }

        _runtimeState.OnDisconnected();
        Logger.Warn("Unity websocket disconnected");
    }

    private static async Task SendRawAsync(WebSocket socket, JsonObject payload, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            throw new UnityMcpException(ErrorCodes.UnityDisconnected, "Unity websocket is not connected");
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
        catch (UnityMcpException ex)
        {
            return ToolResultFormatter.Error(ex);
        }
        catch (Exception ex)
        {
            return ToolResultFormatter.Error(new UnityMcpException(
                ErrorCodes.UnityExecution,
                "Unexpected server error",
                new JsonObject { ["message"] = ex.Message }));
        }
    }

    private async Task<JsonNode> ExecuteToolAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        return toolName switch
        {
            "get_editor_state" => _runtimeState.GetSnapshot().ToJson(),
            "read_console" => await _unityBridge.ReadConsoleAsync(ParseMaxEntries(arguments), cancellationToken),
            "run_tests" => await _unityBridge.RunTestsAsync(ParseRunMode(arguments), ParseFilter(arguments), cancellationToken),
            "get_job_status" => await _unityBridge.GetJobStatusAsync(ParseJobId(arguments), cancellationToken),
            "cancel_job" => await _unityBridge.CancelJobAsync(ParseJobId(arguments), cancellationToken),
            _ => throw new UnityMcpException(ErrorCodes.UnknownCommand, $"Unknown tool: {toolName}"),
        };
    }

    private static int ParseMaxEntries(JsonObject arguments)
    {
        var maxEntries = JsonHelpers.GetInt(arguments, "max_entries") ?? 200;
        if (maxEntries is < 1 or > 2000)
        {
            throw new UnityMcpException(
                ErrorCodes.InvalidParams,
                "max_entries must be between 1 and 2000",
                new JsonObject { ["max_entries"] = maxEntries });
        }

        return maxEntries;
    }

    private static string ParseRunMode(JsonObject arguments)
    {
        var mode = JsonHelpers.GetString(arguments, "mode") ?? "all";
        if (mode is not ("all" or "edit" or "play"))
        {
            throw new UnityMcpException(
                ErrorCodes.InvalidParams,
                "mode must be one of all|edit|play",
                new JsonObject { ["mode"] = mode });
        }

        return mode;
    }

    private static string? ParseFilter(JsonObject arguments)
    {
        if (!arguments.TryGetPropertyValue("filter", out var node) || node is null)
        {
            return null;
        }

        var filter = JsonHelpers.GetString(arguments, "filter");
        if (string.IsNullOrWhiteSpace(filter))
        {
            throw new UnityMcpException(
                ErrorCodes.InvalidParams,
                "filter must be a non-empty string when provided");
        }

        return filter;
    }

    private static string ParseJobId(JsonObject arguments)
    {
        var jobId = JsonHelpers.GetString(arguments, "job_id");
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new UnityMcpException(ErrorCodes.InvalidParams, "job_id is required");
        }

        return jobId;
    }
}

internal static class ToolResultFormatter
{
    public static JsonObject Success(JsonNode payload)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload.ToJsonString(JsonDefaults.Options),
                },
            },
            ["structuredContent"] = payload.DeepClone(),
        };
    }

    public static JsonObject Error(UnityMcpException error)
    {
        var payload = new JsonObject
        {
            ["code"] = error.Code,
            ["message"] = error.Message,
        };

        if (error.Details is not null)
        {
            payload["details"] = error.Details.DeepClone();
        }

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
}

internal sealed class McpHttpHandler
{
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
        var sessionId = GetSessionId(context.Request);
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.ContainsKey(sessionId))
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
            while (!context.RequestAborted.IsCancellationRequested && _sessions.ContainsKey(sessionId))
            {
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
        var id = JsonHelpers.CloneNode(request["id"]);
        var method = JsonHelpers.GetString(request, "method");
        if (string.IsNullOrWhiteSpace(method))
        {
            return new ProcessResult(JsonRpc.Error(id, -32600, "Invalid Request: method is required"), null);
        }

        var sessionId = GetSessionId(context.Request);
        if (!string.IsNullOrWhiteSpace(sessionId) && !_sessions.ContainsKey(sessionId))
        {
            return new ProcessResult(JsonRpc.Error(id, -32000, "Bad Request: No valid session ID provided"), null);
        }

        if (string.Equals(method, "initialize", StringComparison.Ordinal))
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

        if (string.Equals(method, "notifications/initialized", StringComparison.Ordinal) ||
            method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            return ProcessResult.NoContent();
        }

        if (string.Equals(method, "ping", StringComparison.Ordinal))
        {
            return new ProcessResult(JsonRpc.Result(id, new JsonObject()), null);
        }

        if (string.Equals(method, "tools/list", StringComparison.Ordinal))
        {
            var result = new JsonObject
            {
                ["tools"] = ToolCatalog.BuildMcpTools(),
            };
            return new ProcessResult(JsonRpc.Result(id, result), null);
        }

        if (string.Equals(method, "tools/call", StringComparison.Ordinal))
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

        return new ProcessResult(JsonRpc.Error(id, -32601, $"Method not found: {method}"), null);
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
}

internal static class JsonRpc
{
    public static JsonObject Result(JsonNode? id, JsonNode result)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result.DeepClone(),
        };
    }

    public static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["data"] = data?.DeepClone(),
            },
        };

        if (data is null)
        {
            var error = payload["error"] as JsonObject;
            error?.Remove("data");
        }

        return payload;
    }

    public static Task WriteAsync(HttpContext context, JsonNode node)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(node.ToJsonString(JsonDefaults.Options));
    }
}

internal static class JsonHelpers
{
    public static string? GetString(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }

    public static int? GetInt(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var number))
        {
            return number;
        }

        return null;
    }

    public static ulong? GetUlong(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<ulong>(out var ulongValue))
            {
                return ulongValue;
            }

            if (jsonValue.TryGetValue<long>(out var longValue) && longValue >= 0)
            {
                return (ulong)longValue;
            }

            if (jsonValue.TryGetValue<int>(out var intValue) && intValue >= 0)
            {
                return (ulong)intValue;
            }
        }

        return null;
    }

    public static JsonObject AsObjectOrEmpty(JsonNode? node)
    {
        return node as JsonObject ?? new JsonObject();
    }

    public static JsonNode? CloneNode(JsonNode? node)
    {
        return node?.DeepClone();
    }
}
