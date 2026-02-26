using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace UnityMcpServer;

internal static class ServerHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        ServerConfig config;
        try
        {
            config = ConfigLoader.Parse(args);
        }
        catch (McpException ex) when (ex.Code == ErrorCodes.ConfigValidation)
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
            var app = BuildApplication(args, config);
            RegisterLifetimeEvents(app, config);

            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("Server crashed", ("error", ex.Message));
            return 1;
        }
    }

    private static WebApplication BuildApplication(string[] args, ServerConfig config)
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

        ConfigureServices(builder.Services, config);

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        var runtimeState = app.Services.GetRequiredService<RuntimeState>();
        runtimeState.SetServerState(ServerState.WaitingEditor);

        MapEndpoints(app);
        return app;
    }

    private static void ConfigureServices(IServiceCollection services, ServerConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton<RuntimeState>();
        services.AddSingleton(_ => new RequestScheduler(Constants.QueueMaxSize));
        services.AddSingleton<UnityBridge>();
        services.AddSingleton<McpToolService>();
        services.AddSingleton<McpHttpHandler>();
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapMethods(Constants.McpHttpPath, new[] { HttpMethods.Post }, static (HttpContext context, McpHttpHandler handler) =>
            handler.HandlePostAsync(context));

        app.MapMethods(Constants.McpHttpPath, new[] { HttpMethods.Get }, static (HttpContext context, McpHttpHandler handler) =>
            handler.HandleGetAsync(context));

        app.MapMethods(Constants.McpHttpPath, new[] { HttpMethods.Delete }, static (HttpContext context, McpHttpHandler handler) =>
            handler.HandleDeleteAsync(context));

        app.Map(Constants.UnityWsPath, static (HttpContext context, UnityBridge bridge) =>
            bridge.HandleWebSocketEndpointAsync(context));
    }

    private static void RegisterLifetimeEvents(WebApplication app, ServerConfig config)
    {
        var runtimeState = app.Services.GetRequiredService<RuntimeState>();

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
    }
}
