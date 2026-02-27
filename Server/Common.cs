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
