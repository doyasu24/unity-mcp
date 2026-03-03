using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UnityMcpPlugin
{
    internal sealed class CommandRouter
    {
        private readonly CommandExecutor _commandExecutor;
        private readonly Func<object, CancellationToken, Task> _sendMessageAsync;
        private readonly Func<string, string, string, CancellationToken, Task> _sendProtocolErrorAsync;

        internal CommandRouter(
            CommandExecutor commandExecutor,
            Func<object, CancellationToken, Task> sendMessageAsync,
            Func<string, string, string, CancellationToken, Task> sendProtocolErrorAsync)
        {
            _commandExecutor = commandExecutor;
            _sendMessageAsync = sendMessageAsync;
            _sendProtocolErrorAsync = sendProtocolErrorAsync;
        }

        internal async Task RouteAsync(string type, JObject message, CancellationToken cancellationToken)
        {
            switch (type)
            {
                case "hello":
                    HandleServerHello(message);
                    return;
                case "capability":
                    HandleServerCapability(message);
                    return;
                case "execute":
                    await HandleExecuteAsync(message, cancellationToken);
                    return;
                case "error":
                    PluginLogger.DevWarn("Received error from server", ("payload", Payload.ToJson(message)));
                    return;
                default:
                    var requestId = Payload.GetString(message, "request_id");
                    await _sendProtocolErrorAsync(requestId, "ERR_UNKNOWN_COMMAND", $"unknown command type: {type}", cancellationToken);
                    return;
            }
        }

        private static void HandleServerHello(JObject message)
        {
            var serverVersion = Payload.GetString(message, "server_version") ?? "unknown";
            PluginLogger.DevInfo("Received server hello", ("server_version", serverVersion));
        }

        private static void HandleServerCapability(JObject message)
        {
            if (!Payload.TryGetArrayLength(message, "tools", out var count))
            {
                PluginLogger.DevInfo("Received capability without tools");
                return;
            }

            PluginLogger.DevInfo("Received capability", ("tool_count", count));
        }

        private async Task HandleExecuteAsync(JObject message, CancellationToken cancellationToken)
        {
            var requestId = Payload.GetString(message, "request_id");
            var toolName = Payload.GetString(message, "tool_name");
            var parameters = Payload.GetObjectOrEmpty(message, "params");

            if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(toolName))
            {
                await _sendProtocolErrorAsync(requestId, "ERR_INVALID_REQUEST", "request_id and tool_name are required", cancellationToken);
                return;
            }

            try
            {
                var result = await _commandExecutor.ExecuteToolAsync(toolName, parameters);
                await _sendMessageAsync(new
                {
                    type = "result",
                    request_id = requestId,
                    status = "ok",
                    result,
                }, cancellationToken);
            }
            catch (PluginException ex)
            {
                await _sendMessageAsync(new
                {
                    type = "result",
                    request_id = requestId,
                    status = "error",
                    result = new
                    {
                        code = ex.Code,
                        message = ex.Message,
                    },
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                await _sendMessageAsync(new
                {
                    type = "result",
                    request_id = requestId,
                    status = "error",
                    result = new
                    {
                        code = "ERR_UNITY_EXECUTION",
                        message = ex.Message,
                    },
                }, cancellationToken);
            }
        }

    }
}
