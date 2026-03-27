using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace UnityMcpServer;

internal static class ServerHost
{
    public static async Task RunAsync(int port, LogLevel logLevel, CancellationToken ct)
    {
        var app = BuildApplication(port, logLevel);
        RegisterLifetimeEvents(app, port);

        await app.RunAsync(ct);
    }

    private static WebApplication BuildApplication(int port, LogLevel logLevel)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromMilliseconds(300);
        });

        // ZLogger: 全ログを stderr に出力 (MCP プロトコルが stdout を使うため)
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(logLevel);
        builder.Logging.AddZLoggerConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
            options.UsePlainTextFormatter(formatter =>
            {
                // ANSI カラー付きプレフィックス: タイムスタンプ(灰)、レベル(レベル別色)
                // e.g. "2026-03-24T03:01:00.166 INFO  Unity connected ..."
                formatter.SetPrefixFormatter($"{0} {1} ",
                    (in MessageTemplate template, in LogInfo info) =>
                    {
                        var ts = $"\x1b[90m{info.Timestamp.Utc:yyyy-MM-ddTHH:mm:ss.fff}\x1b[0m";
                        var level = info.LogLevel switch
                        {
                            LogLevel.Debug => "\x1b[90mDEBUG\x1b[0m",
                            LogLevel.Warning => "\x1b[33mWARN \x1b[0m",
                            LogLevel.Error => "\x1b[31mERROR\x1b[0m",
                            LogLevel.Critical => "\x1b[31mFATAL\x1b[0m",
                            _ => "INFO ",
                        };
                        template.Format(ts, level);
                    });
            });
        });
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, port);
        });

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        var config = new ServerConfig(port);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<RuntimeState>();
        builder.Services.AddSingleton<UnityBridge>();
        builder.Services.AddSingleton<McpToolService>();
        builder.Services.AddSingleton<McpHttpHandler>();

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(10),
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

        return app;
    }

    private static void RegisterLifetimeEvents(WebApplication app, int port)
    {
        var runtimeState = app.Services.GetRequiredService<RuntimeState>();
        var bridge = app.Services.GetRequiredService<UnityBridge>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ServerHost));

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            WriteBanner(port);
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            runtimeState.SetServerState(ServerState.Stopping);
            bridge.BeginShutdown();
            logger.ZLogInformation($"Server stopping");
        });

        app.Lifetime.ApplicationStopped.Register(() =>
        {
            runtimeState.SetServerState(ServerState.Stopped);
            logger.ZLogInformation($"Server stopped");
        });
    }

    private static void WriteBanner(int port)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  \x1b[1;35m unity-mcp \x1b[0m\x1b[90mv{Constants.ServerVersion}\x1b[0m");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  \x1b[90mMCP  \x1b[0mhttp://{Constants.Host}:{port}{Constants.McpHttpPath}");
        Console.Error.WriteLine($"  \x1b[90mWS   \x1b[0mws://{Constants.Host}:{port}{Constants.UnityWsPath}");
        Console.Error.WriteLine();
    }
}
