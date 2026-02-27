# Editor Playback Tools 仕様 v1

- Status: Draft
- Date: 2026-02-27
- Target: `Server` / `UnityMCPPlugin`

## 1. 目的
MCP から Unity Editor の Play Mode 操作（再生・停止・一時停止）を実行できるようにする。

## 2. 追加する tool
1. `get_play_mode_state` (read-only)
2. `control_play_mode` (edit)

両方とも `execution_mode=sync` とし、job 化しない。

## 3. 入力仕様
### 3.1 `get_play_mode_state` (read-only)

```json
{
  "type": "object",
  "properties": {},
  "additionalProperties": false
}
```

### 3.2 `control_play_mode` (edit)

`action` 必須。

```json
{
  "type": "object",
  "properties": {
    "action": {
      "type": "string",
      "enum": ["start", "stop", "pause"]
    }
  },
  "required": ["action"],
  "additionalProperties": false
}
```

## 4. 出力仕様
### 4.1 `get_play_mode_state` (read-only)

```json
{
  "state": "playing|paused|stopped",
  "is_playing": false,
  "is_paused": false,
  "is_playing_or_will_change_playmode": false
}
```

### 4.2 `control_play_mode` (edit)

以下を返す。

```json
{
  "action": "start|stop|pause",
  "accepted": true,
  "is_playing": true,
  "is_paused": false,
  "is_playing_or_will_change_playmode": true
}
```

フィールド定義:
1. `state`: 現在状態（`playing` / `paused` / `stopped`）
2. `action`: 実行した操作種別（入力と同値）
3. `accepted`: 操作要求を受理したか。v1 では成功時は常に `true`
4. `is_playing`: 返却時点の `EditorApplication.isPlaying`
5. `is_paused`: 返却時点の `EditorApplication.isPaused`
6. `is_playing_or_will_change_playmode`: 返却時点の `EditorApplication.isPlayingOrWillChangePlaymode`

`state` の判定:
- `is_playing=true && is_paused=true` -> `paused`
- `is_playing=true && is_paused=false` -> `playing`
- それ以外 -> `stopped`

## 5. 動作ルール
1. `get_play_mode_state` は読み取りのみで状態変更しない。
2. 実処理は Unity メインスレッドで実行する。
3. `control_play_mode` の `action=start`:
   - `EditorApplication.isPaused = false`
   - `EditorApplication.isPlaying = true`
4. `control_play_mode` の `action=stop`:
   - `EditorApplication.isPaused = false`
   - `EditorApplication.isPlaying = false`
5. `control_play_mode` の `action=pause`:
   - `EditorApplication.isPlaying == true` のときのみ許可
   - 条件を満たす場合は `EditorApplication.isPaused = true`
   - Play Mode でない場合は `ERR_INVALID_STATE` を返す
6. `control_play_mode` は Play Mode の遷移完了を待たない。要求反映直後の観測値を返す。

## 6. メタデータ
`capability.tools` の metadata は以下で固定する。

### 6.1 `get_play_mode_state` (read-only)
1. `execution_mode`: `sync`
2. `supports_cancel`: `false`
3. `default_timeout_ms`: `5000`
4. `max_timeout_ms`: `10000`
5. `requires_client_request_id`: `false`
6. `execution_error_retryable`: `true`

### 6.2 `control_play_mode` (edit)
1. `execution_mode`: `sync`
2. `supports_cancel`: `false`
3. `default_timeout_ms`: `10000`
4. `max_timeout_ms`: `30000`
5. `requires_client_request_id`: `false`
6. `execution_error_retryable`: `false`

## 7. エラー仕様
1. `action=pause` を Play Mode 外で呼んだ場合、Plugin は `ERR_INVALID_STATE` を返す。
2. Server から MCP クライアントへのエラー契約は既存通り `ERR_UNITY_EXECUTION` を使用し、詳細は `details` に含める。
3. `waiting_editor` など実行前失敗は既存ルール（`ERR_EDITOR_NOT_READY` など）を維持する。
