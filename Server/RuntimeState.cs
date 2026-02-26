using System.Text.Json.Serialization;

namespace UnityMcpServer;

internal enum ServerState
{
    Booting,
    WaitingEditor,
    Ready,
    Stopping,
    Stopped,
}

internal enum EditorState
{
    Unknown,
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

internal static class WireState
{
    public static string ToWire(this ServerState state) => state switch
    {
        ServerState.Booting => "booting",
        ServerState.WaitingEditor => "waiting_editor",
        ServerState.Ready => "ready",
        ServerState.Stopping => "stopping",
        ServerState.Stopped => "stopped",
        _ => "stopped",
    };

    public static string ToWire(this EditorState state) => state switch
    {
        EditorState.Unknown => "unknown",
        EditorState.Ready => "ready",
        EditorState.Compiling => "compiling",
        EditorState.Reloading => "reloading",
        _ => "unknown",
    };

    public static string ToWire(this JobState state) => state switch
    {
        JobState.Queued => "queued",
        JobState.Running => "running",
        JobState.Succeeded => "succeeded",
        JobState.Failed => "failed",
        JobState.Timeout => "timeout",
        JobState.Cancelled => "cancelled",
        _ => "failed",
    };

    public static EditorState ParseEditorState(string? value) => value switch
    {
        "ready" => EditorState.Ready,
        "compiling" => EditorState.Compiling,
        "reloading" => EditorState.Reloading,
        _ => EditorState.Unknown,
    };

    public static bool TryParseJobState(string? value, out JobState state)
    {
        state = value switch
        {
            "queued" => JobState.Queued,
            "running" => JobState.Running,
            "succeeded" => JobState.Succeeded,
            "failed" => JobState.Failed,
            "timeout" => JobState.Timeout,
            "cancelled" => JobState.Cancelled,
            _ => JobState.Failed,
        };

        return value is "queued" or "running" or "succeeded" or "failed" or "timeout" or "cancelled";
    }

    public static bool IsTerminal(JobState state) => state is JobState.Succeeded or JobState.Failed or JobState.Timeout or JobState.Cancelled;
}

internal sealed record RuntimeSnapshot(
    [property: JsonPropertyName("server_state")] string ServerState,
    [property: JsonPropertyName("editor_state")] string EditorState,
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("last_editor_status_seq")] ulong LastEditorStatusSeq);

internal sealed class RuntimeState
{
    private readonly object _gate = new();
    private ServerState _serverState = ServerState.Booting;
    private EditorState _editorState = EditorState.Unknown;
    private bool _connected;
    private ulong _lastEditorStatusSeq;

    public event Action? StateChanged;

    public RuntimeSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new RuntimeSnapshot(
                _serverState.ToWire(),
                _connected ? _editorState.ToWire() : EditorState.Unknown.ToWire(),
                _connected,
                _lastEditorStatusSeq);
        }
    }

    public bool IsEditorReady()
    {
        lock (_gate)
        {
            return _connected && _editorState == EditorState.Ready;
        }
    }

    public void SetServerState(ServerState next)
    {
        var changed = false;
        lock (_gate)
        {
            if (_serverState != next)
            {
                _serverState = next;
                changed = true;
            }
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }
    }

    public void OnConnected(EditorState initialEditorState)
    {
        lock (_gate)
        {
            _connected = true;
            _editorState = initialEditorState;
            _serverState = ServerState.Ready;
        }

        StateChanged?.Invoke();
    }

    public void OnDisconnected()
    {
        lock (_gate)
        {
            _connected = false;
            _editorState = EditorState.Unknown;
            if (_serverState is not ServerState.Stopping and not ServerState.Stopped)
            {
                _serverState = ServerState.WaitingEditor;
            }
        }

        StateChanged?.Invoke();
    }

    public void OnEditorStatus(EditorState state, ulong seq)
    {
        var changed = false;
        lock (_gate)
        {
            if (seq > _lastEditorStatusSeq)
            {
                _lastEditorStatusSeq = seq;
                _editorState = state;
                changed = true;
            }
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }
    }

    public void OnPong(EditorState? state, ulong? seq)
    {
        lock (_gate)
        {
            if (state.HasValue)
            {
                _editorState = state.Value;
            }

            if (seq.HasValue && seq.Value > _lastEditorStatusSeq)
            {
                _lastEditorStatusSeq = seq.Value;
            }
        }

        StateChanged?.Invoke();
    }

    public async Task<bool> WaitForEditorReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsEditorReady())
        {
            return true;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler()
        {
            if (IsEditorReady())
            {
                tcs.TrySetResult(true);
            }
        }

        StateChanged += Handler;
        try
        {
            if (IsEditorReady())
            {
                return true;
            }

            var delayTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, delayTask);
            if (completed == tcs.Task)
            {
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            StateChanged -= Handler;
        }
    }
}
