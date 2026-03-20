using System.Collections.Generic;
using Newtonsoft.Json;

namespace UnityMcpPlugin
{
    internal enum EditorBridgeState
    {
        Ready,
        Compiling,
        Reloading,
        EnteringPlayMode,
        ExitingPlayMode,
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
        [property: JsonProperty("stack_trace", NullValueHandling = NullValueHandling.Ignore)] string StackTrace,
        [property: JsonProperty("count", NullValueHandling = NullValueHandling.Ignore)] int? Count);

    internal sealed record TypeSummary(
        [property: JsonProperty("log")] int Log,
        [property: JsonProperty("warning")] int Warning,
        [property: JsonProperty("error")] int Error,
        [property: JsonProperty("assert")] int Assert,
        [property: JsonProperty("exception")] int Exception);

    internal sealed record ReadConsolePayload(
        [property: JsonProperty("entries")] IReadOnlyList<ConsoleEntry> Entries,
        [property: JsonProperty("count")] int Count,
        [property: JsonProperty("total_count")] int TotalCount,
        [property: JsonProperty("truncated")] bool Truncated,
        [property: JsonProperty("next_offset", NullValueHandling = NullValueHandling.Ignore)] int? NextOffset,
        [property: JsonProperty("type_summary")] TypeSummary TypeSummary);

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

    internal sealed record SceneEntry(
        [property: JsonProperty("path")] string Path);

    internal sealed record ListScenesPayload(
        [property: JsonProperty("scenes")] IReadOnlyList<SceneEntry> Scenes,
        [property: JsonProperty("count")] int Count,
        [property: JsonProperty("total_count")] int TotalCount,
        [property: JsonProperty("truncated")] bool Truncated,
        [property: JsonProperty("next_offset", NullValueHandling = NullValueHandling.Ignore)] int? NextOffset);

    internal sealed record OpenScenePayload(
        [property: JsonProperty("path")] string Path,
        [property: JsonProperty("mode")] string Mode);

    internal sealed record SaveScenePayload(
        [property: JsonProperty("path")] string Path);

    internal sealed record CreateScenePayload(
        [property: JsonProperty("path")] string Path);

    internal sealed record AssetEntry(
        [property: JsonProperty("path")] string Path,
        [property: JsonProperty("type")] string Type,
        [property: JsonProperty("name")] string Name,
        [property: JsonProperty("guid")] string Guid);

    internal sealed record FindAssetsPayload(
        [property: JsonProperty("assets")] IReadOnlyList<AssetEntry> Assets,
        [property: JsonProperty("count")] int Count,
        [property: JsonProperty("truncated")] bool Truncated,
        [property: JsonProperty("total_count")] int TotalCount,
        [property: JsonProperty("next_offset", NullValueHandling = NullValueHandling.Ignore)] int? NextOffset);

    internal sealed record SelectedObjectInfo(
        [property: JsonProperty("name")] string Name,
        [property: JsonProperty("instance_id")] int InstanceId,
        [property: JsonProperty("type")] string Type,
        [property: JsonProperty("path")] string Path);

    internal sealed record GetSelectionPayload(
        [property: JsonProperty("active_object")] SelectedObjectInfo ActiveObject,
        [property: JsonProperty("selected_objects")] IReadOnlyList<SelectedObjectInfo> SelectedObjects,
        [property: JsonProperty("count")] int Count);

    internal sealed record SetSelectionPayload(
        [property: JsonProperty("selected")] bool Selected,
        [property: JsonProperty("count")] int Count);

    internal sealed record FoundGameObject(
        [property: JsonProperty("name")] string Name,
        [property: JsonProperty("path")] string Path,
        [property: JsonProperty("tag")] string Tag,
        [property: JsonProperty("layer")] int Layer,
        [property: JsonProperty("active")] bool Active,
        [property: JsonProperty("components")] IReadOnlyList<string> Components);

    internal sealed record FindSceneGameObjectsPayload(
        [property: JsonProperty("game_objects")] IReadOnlyList<FoundGameObject> GameObjects,
        [property: JsonProperty("count")] int Count,
        [property: JsonProperty("total_count")] int TotalCount,
        [property: JsonProperty("truncated")] bool Truncated,
        [property: JsonProperty("next_offset", NullValueHandling = NullValueHandling.Ignore)] int? NextOffset);

    internal sealed record InstantiatePrefabPayload(
        [property: JsonProperty("path")] string Path,
        [property: JsonProperty("name")] string Name,
        [property: JsonProperty("prefab_asset_path")] string PrefabAssetPath,
        [property: JsonProperty("instance_id")] int InstanceId);

    internal sealed record GetAssetInfoPayload(
        [property: JsonProperty("path")] string Path,
        [property: JsonProperty("name")] string Name,
        [property: JsonProperty("type")] string Type,
        [property: JsonProperty("guid")] string Guid,
        [property: JsonProperty("file_size_bytes")] long FileSizeBytes,
        [property: JsonProperty("properties")] Newtonsoft.Json.Linq.JObject Properties);

    internal sealed record FindPrefabGameObjectsPayload(
        [property: JsonProperty("game_objects")] IReadOnlyList<FoundGameObject> GameObjects,
        [property: JsonProperty("count")] int Count,
        [property: JsonProperty("total_count")] int TotalCount,
        [property: JsonProperty("truncated")] bool Truncated,
        [property: JsonProperty("next_offset", NullValueHandling = NullValueHandling.Ignore)] int? NextOffset);

    internal sealed record ManageAssetPayload(
        [property: JsonProperty("action")] string Action,
        [property: JsonProperty("asset_path")] string AssetPath,
        [property: JsonProperty("asset_type")] string AssetType,
        [property: JsonProperty("success")] bool Success);

    internal sealed record MaterialPropertyInfo(
        [property: JsonProperty("name")] string Name,
        [property: JsonProperty("type")] string Type,
        [property: JsonProperty("value")] object Value,
        [property: JsonProperty("range_min", NullValueHandling = NullValueHandling.Ignore)] float? RangeMin,
        [property: JsonProperty("range_max", NullValueHandling = NullValueHandling.Ignore)] float? RangeMax);

    internal sealed record GetMaterialPropertiesPayload(
        [property: JsonProperty("asset_path")] string AssetPath,
        [property: JsonProperty("shader")] string Shader,
        [property: JsonProperty("render_queue")] int RenderQueue,
        [property: JsonProperty("properties")] IReadOnlyList<MaterialPropertyInfo> Properties,
        [property: JsonProperty("keyword_count")] int KeywordCount);

    internal sealed record SetMaterialPropertiesPayload(
        [property: JsonProperty("asset_path")] string AssetPath,
        [property: JsonProperty("properties_set")] IReadOnlyList<string> PropertiesSet,
        [property: JsonProperty("properties_skipped")] IReadOnlyList<string> PropertiesSkipped);

    internal sealed record SetMaterialShaderPayload(
        [property: JsonProperty("asset_path")] string AssetPath,
        [property: JsonProperty("previous_shader")] string PreviousShader,
        [property: JsonProperty("new_shader")] string NewShader);

    internal sealed record GetMaterialKeywordsPayload(
        [property: JsonProperty("asset_path")] string AssetPath,
        [property: JsonProperty("keywords")] IReadOnlyList<string> Keywords);

    internal sealed record SetMaterialKeywordsPayload(
        [property: JsonProperty("asset_path")] string AssetPath,
        [property: JsonProperty("keywords_action")] string KeywordsAction,
        [property: JsonProperty("keywords_changed")] IReadOnlyList<string> KeywordsChanged);

    internal sealed record CaptureScreenshotPayload(
        [property: JsonProperty("file_path")] string FilePath,
        [property: JsonProperty("width")] int Width,
        [property: JsonProperty("height")] int Height,
        [property: JsonProperty("camera_name")] string CameraName,
        [property: JsonProperty("source")] string Source);

    internal sealed record BatchOperationResult(
        [property: JsonProperty("tool_name")] string ToolName,
        [property: JsonProperty("success")] bool Success,
        [property: JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)] object Result,
        [property: JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] string Error);

    internal sealed record BatchSummary(
        [property: JsonProperty("total")] int Total,
        [property: JsonProperty("succeeded")] int Succeeded,
        [property: JsonProperty("failed")] int Failed,
        [property: JsonProperty("skipped")] int Skipped);

    internal sealed record ExecuteBatchPayload(
        [property: JsonProperty("success")] bool Success,
        [property: JsonProperty("results")] IReadOnlyList<BatchOperationResult> Results,
        [property: JsonProperty("summary")] BatchSummary Summary,
        [property: JsonProperty("atomic")] bool Atomic,
        [property: JsonProperty("rolled_back")] bool RolledBack);

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
