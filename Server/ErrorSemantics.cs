using System.Text.Json.Nodes;

namespace UnityMcpServer;

internal enum DispatchStage
{
    BeforeSend,
    AfterSend,
    Completed,
}

internal readonly record struct ErrorSemantics(bool Retryable, string ExecutionGuarantee, string RecoveryAction);

internal static class ExecutionGuarantees
{
    public const string NotExecuted = "not_executed";
    public const string Unknown = "unknown";
}

internal static class RecoveryActions
{
    public const string RetryAllowed = "retry_allowed";
    public const string InspectStateThenRetryIfNeeded = "inspect_state_then_retry_if_needed";
}

internal static class ErrorSemanticsResolver
{
    public static ErrorSemantics Resolve(string errorCode)
    {
        return errorCode switch
        {
            ErrorCodes.EditorNotReady => new ErrorSemantics(true, ExecutionGuarantees.NotExecuted, RecoveryActions.RetryAllowed),
            ErrorCodes.CompileTimeout => new ErrorSemantics(true, ExecutionGuarantees.NotExecuted, RecoveryActions.RetryAllowed),
            ErrorCodes.UnityDisconnected => new ErrorSemantics(true, ExecutionGuarantees.NotExecuted, RecoveryActions.RetryAllowed),
            ErrorCodes.ReconnectTimeout => new ErrorSemantics(false, ExecutionGuarantees.Unknown, RecoveryActions.InspectStateThenRetryIfNeeded),
            ErrorCodes.RequestTimeout => new ErrorSemantics(false, ExecutionGuarantees.Unknown, RecoveryActions.InspectStateThenRetryIfNeeded),
            _ => new ErrorSemantics(false, ExecutionGuarantees.Unknown, RecoveryActions.InspectStateThenRetryIfNeeded),
        };
    }

    public static McpException NormalizeDispatchFailure(McpException error, DispatchStage stage)
    {
        if (error.Code == ErrorCodes.UnityDisconnected && stage == DispatchStage.AfterSend)
        {
            return new McpException(
                ErrorCodes.ReconnectTimeout,
                "Unity websocket disconnected after request dispatch",
                MergeDetailsWithDispatchStage(error.Details, stage));
        }

        if ((error.Code == ErrorCodes.UnityDisconnected || error.Code == ErrorCodes.RequestTimeout) &&
            stage is DispatchStage.BeforeSend or DispatchStage.AfterSend)
        {
            return new McpException(
                error.Code,
                error.Message,
                MergeDetailsWithDispatchStage(error.Details, stage));
        }

        return error;
    }

    public static JsonObject EnsureFailureDetails(JsonNode? existingDetails, string executionGuarantee, string recoveryAction)
    {
        var details = JsonHelpers.AsObjectOrEmpty(existingDetails);
        details["execution_guarantee"] = executionGuarantee;
        details["recovery_action"] = recoveryAction;
        return details;
    }

    private static JsonObject MergeDetailsWithDispatchStage(JsonNode? existingDetails, DispatchStage stage)
    {
        var details = JsonHelpers.AsObjectOrEmpty(existingDetails);
        details["dispatch_stage"] = stage switch
        {
            DispatchStage.BeforeSend => "before_send",
            DispatchStage.AfterSend => "after_send",
            DispatchStage.Completed => "completed",
            _ => "before_send",
        };
        return details;
    }
}
