using System.Text.Json.Nodes;
using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class ErrorSemanticsResolverTests
{
    [Fact]
    public void Resolve_ReturnsRetryableNotExecuted_ForEditorNotReady()
    {
        var semantics = ErrorSemanticsResolver.Resolve(ErrorCodes.EditorNotReady);

        Assert.True(semantics.Retryable);
        Assert.Equal(ExecutionGuarantees.NotExecuted, semantics.ExecutionGuarantee);
        Assert.Equal(RecoveryActions.RetryAllowed, semantics.RecoveryAction);
    }

    [Fact]
    public void Resolve_ReturnsUnknownAndNotRetryable_ForReconnectTimeout()
    {
        var semantics = ErrorSemanticsResolver.Resolve(ErrorCodes.ReconnectTimeout);

        Assert.False(semantics.Retryable);
        Assert.Equal(ExecutionGuarantees.Unknown, semantics.ExecutionGuarantee);
        Assert.Equal(RecoveryActions.InspectStateThenRetryIfNeeded, semantics.RecoveryAction);
    }

    [Fact]
    public void NormalizeDispatchFailure_MapsAfterSendDisconnect_ToReconnectTimeout()
    {
        var source = new McpException(ErrorCodes.UnityDisconnected, "disconnected");

        var normalized = ErrorSemanticsResolver.NormalizeDispatchFailure(source, DispatchStage.AfterSend);

        Assert.Equal(ErrorCodes.ReconnectTimeout, normalized.Code);
        var details = Assert.IsType<JsonObject>(normalized.Details);
        Assert.Equal("after_send", details["dispatch_stage"]?.GetValue<string>());
    }

    [Fact]
    public void NormalizeDispatchFailure_KeepsBeforeSendDisconnect_AsUnityDisconnected()
    {
        var source = new McpException(ErrorCodes.UnityDisconnected, "disconnected");

        var normalized = ErrorSemanticsResolver.NormalizeDispatchFailure(source, DispatchStage.BeforeSend);

        Assert.Equal(ErrorCodes.UnityDisconnected, normalized.Code);
        var details = Assert.IsType<JsonObject>(normalized.Details);
        Assert.Equal("before_send", details["dispatch_stage"]?.GetValue<string>());
    }
}
