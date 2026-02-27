using System.Collections.Generic;
using Newtonsoft.Json;

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
        [property: JsonProperty("server_state")] string ServerState,
        [property: JsonProperty("editor_state")] string EditorState,
        [property: JsonProperty("connected")] bool Connected,
        [property: JsonProperty("last_editor_status_seq")] ulong LastEditorStatusSeq);

    internal sealed record ConsoleEntry(
        [property: JsonProperty("type")] string Type,
        [property: JsonProperty("message")] string Message,
        [property: JsonProperty("stack_trace")] string StackTrace);

    internal sealed record ReadConsolePayload(
        [property: JsonProperty("entries")] IReadOnlyList<ConsoleEntry> Entries,
        [property: JsonProperty("count")] int Count,
        [property: JsonProperty("truncated")] bool Truncated);

    internal sealed record ClearConsolePayload(
        [property: JsonProperty("cleared")] bool Cleared,
        [property: JsonProperty("cleared_count")] int ClearedCount);

    internal sealed record RefreshAssetsPayload(
        [property: JsonProperty("refreshed")] bool Refreshed);

    internal sealed record PlayModeStatePayload(
        [property: JsonProperty("state")] string State,
        [property: JsonProperty("is_playing")] bool IsPlaying,
        [property: JsonProperty("is_paused")] bool IsPaused,
        [property: JsonProperty("is_playing_or_will_change_playmode")] bool IsPlayingOrWillChangePlaymode);

    internal sealed record PlayModeControlPayload(
        [property: JsonProperty("action")] string Action,
        [property: JsonProperty("accepted")] bool Accepted,
        [property: JsonProperty("is_playing")] bool IsPlaying,
        [property: JsonProperty("is_paused")] bool IsPaused,
        [property: JsonProperty("is_playing_or_will_change_playmode")] bool IsPlayingOrWillChangePlaymode);

    internal sealed record TestSummary(
        [property: JsonProperty("total")] int Total,
        [property: JsonProperty("passed")] int Passed,
        [property: JsonProperty("failed")] int Failed,
        [property: JsonProperty("skipped")] int Skipped,
        [property: JsonProperty("duration_ms")] int DurationMs);

    internal sealed record FailedTest(
        [property: JsonProperty("name")] string Name,
        [property: JsonProperty("message")] string Message,
        [property: JsonProperty("stack_trace")] string StackTrace);

    internal sealed record RunTestsJobResult(
        [property: JsonProperty("summary")] TestSummary Summary,
        [property: JsonProperty("failed_tests")] IReadOnlyList<FailedTest> FailedTests,
        [property: JsonProperty("mode")] string Mode,
        [property: JsonProperty("filter")] string Filter)
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
