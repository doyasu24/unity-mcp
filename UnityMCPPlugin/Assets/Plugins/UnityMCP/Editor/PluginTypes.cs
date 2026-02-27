using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UnityMcpPlugin
{
    internal enum EditorBridgeState
    {
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

    internal enum PortReconfigureStatus
    {
        Applied,
        RolledBack,
        Failed,
    }

    internal readonly struct PortReconfigureResult
    {
        internal PortReconfigureResult(PortReconfigureStatus status, int activePort, string errorCode, string errorMessage)
        {
            Status = status;
            ActivePort = activePort;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        internal PortReconfigureStatus Status { get; }
        internal int ActivePort { get; }
        internal string ErrorCode { get; }
        internal string ErrorMessage { get; }

        internal static PortReconfigureResult Applied(int activePort)
        {
            return new(PortReconfigureStatus.Applied, activePort, string.Empty, string.Empty);
        }

        internal static PortReconfigureResult RolledBack(int activePort, string errorCode, string errorMessage)
        {
            return new(PortReconfigureStatus.RolledBack, activePort, errorCode, errorMessage);
        }

        internal static PortReconfigureResult Failed(int activePort, string errorCode, string errorMessage)
        {
            return new(PortReconfigureStatus.Failed, activePort, errorCode, errorMessage);
        }
    }

    internal readonly struct EditorSnapshot
    {
        internal EditorSnapshot(bool connected, EditorBridgeState state, ulong seq)
        {
            Connected = connected;
            State = state;
            Seq = seq;
        }

        internal bool Connected { get; }
        internal EditorBridgeState State { get; }
        internal ulong Seq { get; }
    }

    internal readonly struct EditorStateChange
    {
        internal EditorStateChange(bool changed, EditorBridgeState state, ulong seq)
        {
            Changed = changed;
            State = state;
            Seq = seq;
        }

        internal bool Changed { get; }
        internal EditorBridgeState State { get; }
        internal ulong Seq { get; }
    }

    internal sealed record RuntimeStatePayload(
        [property: JsonPropertyName("server_state")] string ServerState,
        [property: JsonPropertyName("editor_state")] string EditorState,
        [property: JsonPropertyName("connected")] bool Connected,
        [property: JsonPropertyName("last_editor_status_seq")] ulong LastEditorStatusSeq);

    internal sealed record ConsoleEntry(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("stack_trace")] string StackTrace);

    internal sealed record ReadConsolePayload(
        [property: JsonPropertyName("entries")] IReadOnlyList<ConsoleEntry> Entries,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("truncated")] bool Truncated);

    internal sealed record ClearConsolePayload(
        [property: JsonPropertyName("cleared")] bool Cleared,
        [property: JsonPropertyName("cleared_count")] int ClearedCount);

    internal sealed record RefreshAssetsPayload(
        [property: JsonPropertyName("refreshed")] bool Refreshed);

    internal sealed record TestSummary(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("passed")] int Passed,
        [property: JsonPropertyName("failed")] int Failed,
        [property: JsonPropertyName("skipped")] int Skipped,
        [property: JsonPropertyName("duration_ms")] int DurationMs);

    internal sealed record FailedTest(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("stack_trace")] string StackTrace);

    internal sealed record RunTestsJobResult(
        [property: JsonPropertyName("summary")] TestSummary Summary,
        [property: JsonPropertyName("failed_tests")] IReadOnlyList<FailedTest> FailedTests,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("filter")] string Filter)
    {
        internal static RunTestsJobResult Empty(string mode, string filter)
        {
            return new RunTestsJobResult(
                new TestSummary(0, 0, 0, 0, 0),
                new List<FailedTest>(),
                mode,
                filter);
        }
    }
}
