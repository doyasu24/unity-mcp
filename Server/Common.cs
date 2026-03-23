using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnityMcpServer;

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
    public const int StaleConnectionTimeoutMs = 15_000;
    public const int EditorStatusIntervalMs = 5_000;
    public const int RequestReconnectWaitMs = 45000;
    public const int CompileGraceTimeoutMs = 90000;
    /// <summary>
    /// ミューテーション操作後にコンパイル開始を待つウィンドウ (ms)。
    /// Plugin は compilationStarted コールバックで即座に editor_status(compiling) を送信するため、
    /// 500ms 以内に検知可能。isCompiling が true なら即座にスキップされる。
    /// </summary>
    public const int PostMutationSettleMs = 500;
    public const string McpSessionHeader = "Mcp-Session-Id";
    public const string DefaultMcpProtocolVersion = "2025-03-26";
}

internal static class ErrorCodes
{

    public const string InvalidRequest = "ERR_INVALID_REQUEST";
    public const string InvalidParams = "ERR_INVALID_PARAMS";
    public const string UnknownCommand = "ERR_UNKNOWN_COMMAND";
    public const string EditorNotReady = "ERR_EDITOR_NOT_READY";
    public const string CompileTimeout = "ERR_COMPILE_TIMEOUT";
    public const string UnityDisconnected = "ERR_UNITY_DISCONNECTED";
    public const string ReconnectTimeout = "ERR_RECONNECT_TIMEOUT";
    public const string RequestTimeout = "ERR_REQUEST_TIMEOUT";
    public const string QueueFull = "ERR_QUEUE_FULL";
    public const string UnityExecution = "ERR_UNITY_EXECUTION";
    public const string InvalidResponse = "ERR_INVALID_RESPONSE";
    public const string CompileErrors = "ERR_COMPILE_ERRORS";
}

internal sealed class McpException : Exception
{
    public McpException(string code, string message, JsonNode? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public JsonNode? Details { get; }
}

/// <summary>
/// 同一カテゴリのログを時間ウィンドウ内で抑制し、抑制件数を次回 emit 時に返す。
/// スレッドセーフ。テスト時は clock を注入可能。
/// </summary>
internal sealed class LogThrottle
{
    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private DateTimeOffset _lastEmitUtc;
    private int _suppressedCount;

    public LogThrottle(TimeSpan interval, Func<DateTimeOffset>? clock = null)
    {
        _interval = interval;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <returns>
    /// <c>ShouldEmit</c>: true なら呼び出し元はログを出力すべき。
    /// <c>Suppressed</c>: 前回 emit から抑制されたイベント数（ShouldEmit=true のときのみ有効）。
    /// </returns>
    public (bool ShouldEmit, int Suppressed) Check()
    {
        lock (_gate)
        {
            var now = _clock();
            if (_lastEmitUtc != default && (now - _lastEmitUtc) < _interval)
            {
                _suppressedCount++;
                return (false, 0);
            }

            var suppressed = _suppressedCount;
            _suppressedCount = 0;
            _lastEmitUtc = now;
            return (true, suppressed);
        }
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}
