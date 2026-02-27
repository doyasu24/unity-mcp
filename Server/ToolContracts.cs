using System.Text.Json.Nodes;

namespace UnityMcpServer;

internal static class ToolNames
{
    public const string GetEditorState = "get_editor_state";
    public const string GetPlayModeState = "get_play_mode_state";
    public const string ReadConsole = "read_console";
    public const string ClearConsole = "clear_console";
    public const string RefreshAssets = "refresh_assets";
    public const string ControlPlayMode = "control_play_mode";
    public const string RunTests = "run_tests";
    public const string GetJobStatus = "get_job_status";
    public const string CancelJob = "cancel_job";
}

internal static class ToolLimits
{
    public const int ReadConsoleDefaultMaxEntries = 200;
    public const int ReadConsoleMinEntries = 1;
    public const int ReadConsoleMaxEntries = 2000;
}

internal static class RunTestsModes
{
    public const string All = "all";
    public const string Edit = "edit";
    public const string Play = "play";

    public static bool IsSupported(string? mode)
    {
        return mode is All or Edit or Play;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(All, Edit, Play);
    }
}

internal static class PlayModeActions
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Pause = "pause";

    public static bool IsSupported(string? action)
    {
        return action is Start or Stop or Pause;
    }

    public static JsonArray ToJsonArray()
    {
        return new JsonArray(Start, Stop, Pause);
    }
}

internal sealed record ReadConsoleRequest(int MaxEntries);

internal sealed record GetPlayModeStateRequest();

internal sealed record ControlPlayModeRequest(string Action);

internal sealed record RunTestsRequest(string Mode, string? Filter);

internal sealed record JobStatusRequest(string JobId);

internal sealed record CancelJobRequest(string JobId);

internal sealed record ReadConsoleResult(JsonNode Payload);

internal sealed record GetPlayModeStateResult(JsonNode Payload);

internal sealed record ClearConsoleResult(JsonNode Payload);

internal sealed record RefreshAssetsResult(JsonNode Payload);

internal sealed record ControlPlayModeResult(JsonNode Payload);

internal sealed record RunTestsResult(string JobId, string State);

internal sealed record JobStatusResult(string JobId, string State, JsonNode? Progress, JsonNode Result);

internal sealed record CancelJobResult(string JobId, string Status);
