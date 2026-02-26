using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityMcpPlugin
{
    internal sealed class CommandRouter
    {
        private readonly CommandExecutor _commandExecutor;
        private readonly JobExecutor _jobExecutor;
        private readonly Func<object, CancellationToken, Task> _sendMessageAsync;
        private readonly Func<string, string, string, CancellationToken, Task> _sendProtocolErrorAsync;

        internal CommandRouter(
            CommandExecutor commandExecutor,
            JobExecutor jobExecutor,
            Func<object, CancellationToken, Task> sendMessageAsync,
            Func<string, string, string, CancellationToken, Task> sendProtocolErrorAsync)
        {
            _commandExecutor = commandExecutor;
            _jobExecutor = jobExecutor;
            _sendMessageAsync = sendMessageAsync;
            _sendProtocolErrorAsync = sendProtocolErrorAsync;
        }

        internal async Task RouteAsync(string type, JsonElement message, CancellationToken cancellationToken)
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
                case "submit_job":
                    await HandleSubmitJobAsync(message, cancellationToken);
                    return;
                case ToolNames.GetJobStatus:
                    await HandleGetJobStatusAsync(message, cancellationToken);
                    return;
                case ToolNames.Cancel:
                    await HandleCancelAsync(message, cancellationToken);
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

        private static void HandleServerHello(JsonElement message)
        {
            var serverVersion = Payload.GetString(message, "server_version") ?? "unknown";
            PluginLogger.DevInfo("Received server hello", ("server_version", serverVersion));
        }

        private static void HandleServerCapability(JsonElement message)
        {
            if (!Payload.TryGetArrayLength(message, "tools", out var count))
            {
                PluginLogger.DevInfo("Received capability without tools");
                return;
            }

            PluginLogger.DevInfo("Received capability", ("tool_count", count));
        }

        private async Task HandleExecuteAsync(JsonElement message, CancellationToken cancellationToken)
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
                // Sync tools are pure in-memory reads and do not require Unity main thread APIs.
                var result = _commandExecutor.ExecuteSyncTool(toolName, parameters);
                await _sendMessageAsync(new
                {
                    type = "result",
                    protocol_version = Wire.ProtocolVersion,
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
                    protocol_version = Wire.ProtocolVersion,
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
                    protocol_version = Wire.ProtocolVersion,
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

        private async Task HandleSubmitJobAsync(JsonElement message, CancellationToken cancellationToken)
        {
            var requestId = Payload.GetString(message, "request_id");
            var toolName = Payload.GetString(message, "tool_name");
            var parameters = Payload.GetObjectOrEmpty(message, "params");

            if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(toolName))
            {
                await _sendProtocolErrorAsync(requestId, "ERR_INVALID_REQUEST", "request_id and tool_name are required", cancellationToken);
                return;
            }

            if (!string.Equals(toolName, ToolNames.RunTests, StringComparison.Ordinal))
            {
                await _sendProtocolErrorAsync(requestId, "ERR_UNKNOWN_COMMAND", $"unsupported job tool: {toolName}", cancellationToken);
                return;
            }

            var mode = Payload.GetString(parameters, "mode") ?? RunTestsModes.All;
            if (!RunTestsModes.IsSupported(mode))
            {
                await _sendProtocolErrorAsync(
                    requestId,
                    "ERR_INVALID_PARAMS",
                    $"mode must be {RunTestsModes.All}|{RunTestsModes.Edit}|{RunTestsModes.Play}",
                    cancellationToken);
                return;
            }

            var filter = Payload.GetString(parameters, "filter") ?? string.Empty;
            var jobId = _jobExecutor.SubmitRunTestsJob(mode, filter);

            await _sendMessageAsync(new
            {
                type = "submit_job_result",
                protocol_version = Wire.ProtocolVersion,
                request_id = requestId,
                status = "accepted",
                job_id = jobId,
            }, cancellationToken);
        }

        private async Task HandleGetJobStatusAsync(JsonElement message, CancellationToken cancellationToken)
        {
            var requestId = Payload.GetString(message, "request_id");
            var jobId = Payload.GetString(message, "job_id");

            if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(jobId))
            {
                await _sendProtocolErrorAsync(requestId, "ERR_INVALID_PARAMS", "request_id and job_id are required", cancellationToken);
                return;
            }

            if (!_jobExecutor.TryGetJobStatus(jobId, out var state, out var result))
            {
                await _sendProtocolErrorAsync(requestId, "ERR_JOB_NOT_FOUND", $"unknown job_id: {jobId}", cancellationToken);
                return;
            }

            await _sendMessageAsync(new
            {
                type = "job_status",
                protocol_version = Wire.ProtocolVersion,
                request_id = requestId,
                job_id = jobId,
                state,
                progress = (object)null,
                result,
            }, cancellationToken);
        }

        private async Task HandleCancelAsync(JsonElement message, CancellationToken cancellationToken)
        {
            var requestId = Payload.GetString(message, "request_id");
            var targetJobId = Payload.GetString(message, "target_job_id");

            if (string.IsNullOrEmpty(requestId))
            {
                await _sendProtocolErrorAsync(requestId, "ERR_INVALID_REQUEST", "request_id is required", cancellationToken);
                return;
            }

            if (string.IsNullOrEmpty(targetJobId))
            {
                await _sendProtocolErrorAsync(requestId, "ERR_CANCEL_NOT_SUPPORTED", "cancel target is not supported", cancellationToken);
                return;
            }

            if (!_jobExecutor.TryCancel(targetJobId, out var status))
            {
                await _sendProtocolErrorAsync(requestId, "ERR_JOB_NOT_FOUND", $"unknown job_id: {targetJobId}", cancellationToken);
                return;
            }

            await _sendMessageAsync(new
            {
                type = "cancel_result",
                protocol_version = Wire.ProtocolVersion,
                request_id = requestId,
                status,
            }, cancellationToken);
        }
    }
}
