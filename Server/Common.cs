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
    public const string ConfigValidation = "ERR_CONFIG_VALIDATION";
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
                    throw new McpException(ErrorCodes.ConfigValidation, "--port requires a value");
                }

                if (!int.TryParse(args[i + 1], out port))
                {
                    throw new McpException(
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
                    throw new McpException(
                        ErrorCodes.ConfigValidation,
                        "--port must be an integer",
                        new JsonObject { ["port"] = raw });
                }
            }
        }

        if (port is < 1 or > 65535)
        {
            throw new McpException(
                ErrorCodes.ConfigValidation,
                "--port must be between 1 and 65535",
                new JsonObject { ["port"] = port });
        }

        return new ServerConfig(port);
    }
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

internal static class Logger
{
    private static readonly object Gate = new();

    public static void Banner(int port)
    {
        lock (Gate)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("  \x1b[1;35m unity-mcp \x1b[0m\x1b[90mv" + Constants.ServerVersion + "\x1b[0m");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  \x1b[90mMCP  \x1b[0mhttp://" + Constants.Host + ":" + port + Constants.McpHttpPath);
            Console.Error.WriteLine("  \x1b[90mWS   \x1b[0mws://" + Constants.Host + ":" + port + Constants.UnityWsPath);
            Console.Error.WriteLine();
        }
    }

    public static void Info(string message, params (string Key, object? Value)[] context) => Write("INFO", message, context);

    public static void Warn(string message, params (string Key, object? Value)[] context) => Write("WARN", message, context);

    public static void Error(string message, params (string Key, object? Value)[] context) => Write("ERROR", message, context);

    private static void Write(string level, string message, IReadOnlyList<(string Key, object? Value)> context)
    {
        var ts = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff");

        var (levelColor, resetColor) = level switch
        {
            "WARN" => ("\x1b[33m", "\x1b[0m"),
            "ERROR" => ("\x1b[31m", "\x1b[0m"),
            _ => ("", ""),
        };

        var sb = new System.Text.StringBuilder();
        sb.Append("\x1b[90m");
        sb.Append(ts);
        sb.Append("\x1b[0m ");
        sb.Append(levelColor);
        sb.Append(level.PadRight(5));
        if (resetColor.Length > 0) sb.Append(resetColor);
        sb.Append(' ');
        sb.Append(message);

        foreach (var (key, value) in context)
        {
            if (value is null)
            {
                continue;
            }

            sb.Append("  \x1b[36m");
            sb.Append(key);
            sb.Append("\x1b[0m=");
            sb.Append(FormatValue(value));
        }

        lock (Gate)
        {
            Console.Error.WriteLine(sb.ToString());
        }
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "null",
            Exception ex => $"{ex.GetType().Name}: {ex.Message}",
            JsonNode node => node.ToJsonString(JsonDefaults.Options),
            _ => value.ToString() ?? "null",
        };
    }
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
