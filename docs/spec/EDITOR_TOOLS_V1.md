# Editor Tools 仕様 v1

- Status: Draft
- Date: 2026-03-02
- Target: `Server` / `UnityMCPPlugin`

## 1. 目的

MCP から Unity Editor の状態確認、Play Mode 制御、コンソール操作、アセットリフレッシュ、テスト実行、スクリーンショット撮影を行えるようにする。

## 2. ツール一覧

| # | ツール名 | 種別 | timeout(ms) | retryable |
|---|---|---|---|---|
| 1 | `get_editor_state` | read-only | 5000/10000 | true |
| 2 | `get_play_mode_state` | read-only | 5000/10000 | true |
| 3 | `control_play_mode` | edit | 10000/30000 | false |
| 4 | `read_console` | read-only | 10000/30000 | true |
| 5 | `clear_console` | edit | 5000/10000 | false |
| 6 | `refresh_assets` | edit | 30000/120000 | false |
| 7 | `run_tests` | edit | 300000/1800000 | false |
| 8 | `capture_screenshot` | read-only | 15000/60000 | true |

timeout 列は `default_timeout_ms / max_timeout_ms`。

---

## 3. `get_editor_state` (read-only)

### 3.1 入力仕様

```json
{
  "type": "object",
  "properties": {},
  "additionalProperties": false
}
```

### 3.2 出力仕様

```json
{
  "server_state": "waiting_editor|ready|stopping|stopped",
  "editor_state": "unknown|ready|compiling|reloading",
  "connected": true,
  "last_editor_status_seq": 42
}
```

フィールド定義:
1. `server_state`: Server の現在状態。`booting` は MCP endpoint 公開前のため通常観測されない
2. `editor_state`: Editor の現在状態。未接続時は `unknown`
3. `connected`: Unity Editor が WebSocket 接続済みか
4. `last_editor_status_seq`: 最後に受信した `editor_status` のシーケンス番号

### 3.3 動作ルール

1. Server 内部の `EditorStateTracker` から状態を取得する。Unity への通信は不要。
2. 読み取りのみで状態変更しない。

### 3.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `5000` |
| `max_timeout_ms` | `10000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `true` |

---

## 4. `get_play_mode_state` (read-only)

### 4.1 入力仕様

```json
{
  "type": "object",
  "properties": {},
  "additionalProperties": false
}
```

### 4.2 出力仕様

```json
{
  "state": "playing|paused|stopped",
  "is_playing": false,
  "is_paused": false,
  "is_playing_or_will_change_playmode": false
}
```

フィールド定義:
1. `state`: 現在状態（`playing` / `paused` / `stopped`）
2. `is_playing`: `EditorApplication.isPlaying`
3. `is_paused`: `EditorApplication.isPaused`
4. `is_playing_or_will_change_playmode`: `EditorApplication.isPlayingOrWillChangePlaymode`

`state` の判定:
- `is_playing=true && is_paused=true` → `paused`
- `is_playing=true && is_paused=false` → `playing`
- それ以外 → `stopped`

### 4.3 動作ルール

1. 読み取りのみで状態変更しない。
2. Unity メインスレッドで実行する。

### 4.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `5000` |
| `max_timeout_ms` | `10000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `true` |

---

## 5. `control_play_mode` (edit)

### 5.1 入力仕様

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

### 5.2 出力仕様

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
1. `action`: 実行した操作種別（入力と同値）
2. `accepted`: 操作要求を受理したか。v1 では成功時は常に `true`
3. `is_playing`: 返却時点の `EditorApplication.isPlaying`
4. `is_paused`: 返却時点の `EditorApplication.isPaused`
5. `is_playing_or_will_change_playmode`: 返却時点の `EditorApplication.isPlayingOrWillChangePlaymode`

### 5.3 動作ルール

1. Unity メインスレッドで実行する。
2. `action=start`:
   - `EditorApplication.isPaused = false`
   - `EditorApplication.isPlaying = true`
3. `action=stop`:
   - `EditorApplication.isPaused = false`
   - `EditorApplication.isPlaying = false`
4. `action=pause`:
   - `EditorApplication.isPlaying == true` のときのみ許可
   - 条件を満たす場合は `EditorApplication.isPaused = true`
   - Play Mode でない場合は `ERR_INVALID_STATE` を返す
5. Play Mode の遷移完了を待たない。要求反映直後の観測値を返す。

### 5.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `false` |

---

## 6. `read_console` (read-only)

### 6.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "max_entries": {
      "type": "integer",
      "minimum": 0,
      "maximum": 2000,
      "default": 10,
      "description": "Maximum number of entries to return. 0 returns counts only."
    },
    "log_type": {
      "type": "array",
      "items": {
        "type": "string",
        "enum": ["log", "warning", "error", "assert", "exception"]
      },
      "description": "Filter by log type. Omit to include all types."
    },
    "message_pattern": {
      "type": "string",
      "description": "Filter entries by message content (regex, case-insensitive)."
    },
    "stack_trace_lines": {
      "type": "integer",
      "minimum": 0,
      "default": 1,
      "description": "Max stack trace lines per entry. 0 omits stack traces entirely."
    },
    "deduplicate": {
      "type": "boolean",
      "default": true,
      "description": "Collapse consecutive identical entries (same type+message) into one with a count field."
    },
    "offset": {
      "type": "integer",
      "minimum": 0,
      "default": 0,
      "description": "Number of matching entries to skip. Use next_offset from truncated responses."
    }
  },
  "additionalProperties": false
}
```

### 6.2 出力仕様

```json
{
  "entries": [
    {
      "type": "error",
      "message": "NullReferenceException: ...",
      "stack_trace": "at PlayerController.Update() ...",
      "count": 3
    }
  ],
  "count": 5,
  "total_count": 50,
  "truncated": true,
  "next_offset": 15,
  "type_summary": {
    "log": 1100,
    "warning": 80,
    "error": 50,
    "assert": 2,
    "exception": 2
  }
}
```

フィールド定義:
1. `entries`: コンソールエントリの配列
2. `count`: 返却エントリ数
3. `total_count`: フィルタ・重複除去適用後の全マッチ件数
4. `truncated`: `offset + count < total_count` の場合 `true`
5. `next_offset`: `truncated` が `true` の場合のみ出力。次のページ取得用 offset 値
6. `type_summary`: 常に出力。`message_pattern` 適用後、`log_type` 適用前のタイプ別件数

エントリフィールド条件:
- `stack_trace`: `stack_trace_lines: 0` の場合フィールド省略。N > 0 の場合、最大 N 行に切り詰め
- `count`: `deduplicate: true`（デフォルト）の場合のみ出力。連続同一エントリの集約数

### 6.3 フィルタリング処理順序

```
全エントリ (LogBuffer)
  → message_pattern (regex, case-insensitive) でフィルタ
  → type_summary を算出
  → log_type でフィルタ
  → total_count を算出
  → deduplicate（連続同一エントリの集約）
  → offset でスキップ
  → max_entries で切り取り
  → stack_trace_lines で stack_trace を切り詰め/除去
  → レスポンス生成
```

### 6.4 動作ルール

1. `max_entries` 省略時は `10`。`0` はカウントのみ返却（entries は空配列）。
2. `max_entries` が `0..2000` の範囲外は `ERR_INVALID_PARAMS`。
3. `log_type` 省略時は全タイプを含む。
4. `message_pattern` は正規表現（case-insensitive）。不正な正規表現は `ERR_INVALID_PARAMS`。
5. `stack_trace_lines` 省略時は `1`。`0` で stack_trace フィールドを省略。
6. `deduplicate` 省略時は `true`。連続する同一タイプ・同一メッセージのエントリを集約。
7. `offset` 省略時は `0`。負の値は `ERR_INVALID_PARAMS`。
8. Plugin 側 `LogBuffer` からエントリを取得する。

### 6.5 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `true` |

---

## 7. `clear_console` (edit)

### 7.1 入力仕様

```json
{
  "type": "object",
  "properties": {},
  "additionalProperties": false
}
```

### 7.2 出力仕様

```json
{
  "cleared": true,
  "cleared_count": 42
}
```

フィールド定義:
1. `cleared`: クリア成功か
2. `cleared_count`: クリアされたエントリ数

### 7.3 動作ルール

1. `LogBuffer.Clear()` で Plugin 側バッファをクリアする。
2. リフレクション経由で Unity Console をクリアする（`LogEntries.Clear()`）。
3. Unity Console のクリアに失敗した場合は `ERR_UNITY_EXECUTION` を返す。

### 7.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `5000` |
| `max_timeout_ms` | `10000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `false` |

---

## 8. `refresh_assets` (edit)

### 8.1 入力仕様

```json
{
  "type": "object",
  "properties": {},
  "additionalProperties": false
}
```

### 8.2 出力仕様

```json
{
  "refreshed": true
}
```

### 8.3 動作ルール

1. `AssetDatabase.Refresh()` を Unity メインスレッドで実行する。
2. ディスク上の変更を Editor に反映する。

### 8.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `30000` |
| `max_timeout_ms` | `120000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `false` |

---

## 9. `run_tests` (edit)

### 9.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "mode": {
      "type": "string",
      "enum": ["all", "edit", "play"],
      "default": "all"
    },
    "filter": {
      "type": "string"
    }
  },
  "additionalProperties": false
}
```

### 9.2 出力仕様

```json
{
  "summary": {
    "total": 10,
    "passed": 9,
    "failed": 1,
    "skipped": 0,
    "duration_ms": 12345
  },
  "failed_tests": [
    {
      "name": "...",
      "message": "...",
      "stack_trace": "..."
    }
  ]
}
```

フィールド定義:
1. `summary`: テスト結果の集約。`total`, `passed`, `failed`, `skipped`, `duration_ms` を含む
2. `failed_tests`: 失敗テストの詳細配列。各要素は `name`, `message`, `stack_trace` を含む

### 9.3 動作ルール

1. `mode` 省略時は `all`。
2. `mode=all` の場合は `edit` と `play` の両方を順に実行し、結果をマージして返す。
3. テスト実行が完了するまでブロックする。タイムアウト時は `ERR_TIMEOUT` を返す。
4. `filter` 指定時は TestRunnerApi のフィルタとして使用する。

### 9.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `300000` |
| `max_timeout_ms` | `1800000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `false` |

---

## 10. `capture_screenshot` (read-only)

### 10.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "source": {
      "type": "string",
      "enum": ["game_view", "scene_view"],
      "default": "game_view",
      "description": "Capture source: 'game_view' or 'scene_view'."
    },
    "width": {
      "type": "integer",
      "minimum": 1,
      "maximum": 7680,
      "default": 1920,
      "description": "Width of the screenshot in pixels."
    },
    "height": {
      "type": "integer",
      "minimum": 1,
      "maximum": 4320,
      "default": 1080,
      "description": "Height of the screenshot in pixels."
    },
    "camera_path": {
      "type": "string",
      "description": "Scene hierarchy path of a Camera to use (game_view only). Defaults to Camera.main."
    },
    "output_path": {
      "type": "string",
      "description": "File path to save the PNG. Defaults to <project>/Screenshots/unity_screenshot_<timestamp>.png."
    }
  },
  "additionalProperties": false
}
```

### 10.2 出力仕様

```json
{
  "output_path": "/path/to/Screenshots/unity_screenshot_20260302_120000.png",
  "width": 1920,
  "height": 1080,
  "camera_name": "Main Camera",
  "source": "game_view"
}
```

### 10.3 動作ルール

1. `source` 省略時は `game_view`。
2. `source=game_view`:
   - `camera_path` 指定時は `GameObjectResolver.Resolve(cameraPath)` でカメラを取得。見つからない場合は `ERR_OBJECT_NOT_FOUND`。
   - `camera_path` 省略時は `Camera.main` を使用。見つからない場合は `ERR_OBJECT_NOT_FOUND`。
3. `source=scene_view`:
   - `SceneView.lastActiveSceneView` からカメラを取得。Scene View が開いていない場合は `ERR_INVALID_STATE`。
4. `output_path` 省略時は `<project>/Screenshots/unity_screenshot_<timestamp>.png`。親ディレクトリが存在しない場合は自動作成する。
5. `RenderTexture` を作成し、カメラでレンダリング後 PNG としてエンコード・保存する。
6. リソースは `try-finally` で確実にクリーンアップする（`RenderTexture`, `Texture2D`, カメラの `targetTexture` 復元）。

### 10.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `15000` |
| `max_timeout_ms` | `60000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `true` |

---

## 11. エラー仕様

| エラーコード | 発生条件 | 対象ツール |
|---|---|---|
| `ERR_INVALID_PARAMS` | パラメータ不正（`max_entries` 範囲外、不正な `action` など） | 全ツール |
| `ERR_INVALID_STATE` | Play Mode 外で `pause` 実行、Scene View 未開放で `scene_view` キャプチャ | `control_play_mode`, `capture_screenshot` |
| `ERR_OBJECT_NOT_FOUND` | カメラが見つからない | `capture_screenshot` |
| `ERR_TIMEOUT` | テスト実行がタイムアウト | `run_tests` |
| `ERR_UNITY_EXECUTION` | Unity Console クリア失敗など実行時エラー | `clear_console` |

Server から MCP クライアントへのエラー契約は `ERR_UNITY_EXECUTION` を使用し、詳細は `details` に含める。`waiting_editor` など実行前失敗は既存ルール（`ERR_EDITOR_NOT_READY` など）を維持する。

---

## 12. 実装参照ファイル

### Server

| ファイル | 内容 |
|---|---|
| `Server/ToolCatalog.cs` | 全 8 ツールのスキーマ・メタデータ定義 |
| `Server/ToolContracts.cs` | `ToolNames`, Request/Result records |
| `Server/Mcp.cs` | MCP tool 呼び出しのパースとルーティング |
| `Server/UnityBridge.cs` | Unity Plugin への通信 |

### Unity Plugin

| ファイル | 内容 |
|---|---|
| `UnityMCPPlugin/.../CommandExecutor.cs` | `read_console`, `get_editor_state`, `get_play_mode_state`, `clear_console`, `refresh_assets`, `control_play_mode`, `run_tests` の実行ロジック |
| `UnityMCPPlugin/.../Tools/CaptureScreenshotTool.cs` | スクリーンショットの撮影・保存実装 |
