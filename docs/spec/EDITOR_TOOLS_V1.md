# Editor Tools 仕様 v1

- Status: Draft
- Date: 2026-03-02
- Target: `Server` / `UnityMCPPlugin`

## 1. 目的

MCP から Unity Editor の状態確認、Play Mode 制御、コンソール操作、アセットリフレッシュ、テスト実行、スクリーンショット撮影を行えるようにする。

## 2. ツール一覧

| # | ツール名 | 種別 | timeout(ms) |
|---|---|---|---|
| 1 | `get_editor_state` | read-only | 5000/10000 |
| 2 | `get_play_mode_state` | read-only | 5000/10000 |
| 3 | `control_play_mode` | edit | 10000/30000 |
| 4 | `read_console` | read-only | 10000/30000 |
| 5 | `clear_console` | edit | 5000/10000 |
| 6 | `refresh_assets` | edit | 120000/300000 |
| 7 | `run_tests` | edit | 300000/1800000 |
| 8 | `capture_screenshot` | read-only | 15000/60000 |
| 9 | `execute_batch` | edit | 600000/2400000 |

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
  "editor_state": "unknown|ready|compiling|reloading|entering_play_mode",
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

1. `AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport)` を Unity メインスレッドで同期実行する。
2. ディスク上の変更を Editor に反映する。
3. コンパイルが発生した場合は `CompilationPipeline.compilationFinished` を待ってからレスポンスを返す。コンパイルが不要な場合は即座に返る。

### 8.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `120000` |
| `max_timeout_ms` | `300000` |
| `requires_client_request_id` | `false` |


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
    "test_full_name": {
      "type": "string",
      "description": "Fully qualified test name for exact match (e.g. 'MyFixture.MyTest(1)'). Maps to TestRunnerApi testNames. Mutually exclusive with test_name_pattern."
    },
    "test_name_pattern": {
      "type": "string",
      "description": "Regex pattern to match test names (e.g. '^MyNamespace\\.' to run all tests in a namespace). Maps to TestRunnerApi groupNames. Mutually exclusive with test_full_name."
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
  ],
  "mode": "all",
  "test_full_name": "",
  "test_name_pattern": ""
}
```

フィールド定義:
1. `summary`: テスト結果の集約。`total`, `passed`, `failed`, `skipped`, `duration_ms` を含む
2. `failed_tests`: 失敗テストの詳細配列。各要素は `name`, `message`, `stack_trace` を含む
3. `mode`: 実行モード
4. `test_full_name`: 指定された完全修飾名（未指定時は空文字列）
5. `test_name_pattern`: 指定された正規表現パターン（未指定時は空文字列）

### 9.3 動作ルール

1. `mode` 省略時は `all`。
2. `mode=all` の場合は `edit` と `play` の両方を順に実行し、結果をマージして返す。
3. テスト実行が完了するまでブロックする。タイムアウト時は `ERR_TIMEOUT` を返す。
4. `test_full_name` 指定時は TestRunnerApi の `testNames`（完全一致）フィルタとして使用する。
5. `test_name_pattern` 指定時は TestRunnerApi の `groupNames`（正規表現）フィルタとして使用する。
6. `test_full_name` と `test_name_pattern` は排他。同時指定時は `ERR_INVALID_PARAMS` を返す。
7. フィルタにマッチするテストがない場合、ハングせず空結果を返す。

### 9.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `300000` |
| `max_timeout_ms` | `1800000` |
| `requires_client_request_id` | `false` |


---

## 10. `capture_screenshot` (read-only)

### 10.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "source": {
      "type": "string",
      "enum": ["game_view", "scene_view", "camera"],
      "default": "game_view",
      "description": "Capture source. 'game_view': composited Game View output (Play Mode) or Camera.main render (Edit Mode). 'scene_view': Scene View camera render. 'camera': render from a specific camera (requires camera_path)."
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
      "description": "Camera hierarchy path. Required for 'camera' source. For 'game_view' (Edit Mode only): overrides Camera.main. Ignored for 'scene_view'."
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

MCP レスポンスの `content` 配列には以下の 2 ブロックが含まれる:
1. `type: "image"` — base64 エンコードされた PNG (`mimeType: "image/png"`)
2. `type: "text"` — JSON メタデータ（下記）

ファイルサイズが 5MB を超える場合、image block は省略されテキストのみとなる。

`structuredContent` は含まれない（`structuredContent` が存在すると一部 MCP クライアントが `content` 配列を無視するため）。

テキストブロックの JSON メタデータ:
```json
{
  "file_path": "/path/to/Screenshots/unity_screenshot_20260302_120000.png",
  "width": 1920,
  "height": 1080,
  "camera_name": "Main Camera",
  "source": "game_view"
}
```

### 10.3 動作ルール

1. `source` 省略時は `game_view`。
2. `source=game_view`:
   - **Play Mode**: `ScreenCapture.CaptureScreenshotAsTexture()` で Game View の合成出力（全カメラ・Canvas UI・ポストプロセス含む）をキャプチャ。`width`/`height`/`camera_path` は無視される（Game View の実解像度で取得）。
   - **Edit Mode**: `camera_path` 指定時は `GameObjectResolver.Resolve(cameraPath)` でカメラを取得。省略時は `Camera.main` を使用。見つからない場合は `ERR_OBJECT_NOT_FOUND`。指定の `width`/`height` でレンダリング。
3. `source=scene_view`:
   - `SceneView.lastActiveSceneView` からカメラを取得。Scene View が開いていない場合は `ERR_INVALID_STATE`。
4. `source=camera`:
   - `camera_path` 必須。指定されたカメラ 1 台をレンダリング。`output_path` 指定時はそのパスをそのまま使用。`camera_path` 省略時は `ERR_INVALID_PARAMS`。
5. `output_path` 省略時は `<project>/Screenshots/unity_screenshot_<timestamp>.png`。親ディレクトリが存在しない場合は自動作成する。
6. `RenderTexture` を作成し、カメラでレンダリング後 PNG としてエンコード・保存する。
7. リソースは `try-finally` で確実にクリーンアップする（`RenderTexture`, `Texture2D`, カメラの `targetTexture` 復元）。

### 10.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `15000` |
| `max_timeout_ms` | `60000` |
| `requires_client_request_id` | `false` |


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

## 13. `execute_batch` (edit)

複数のツール呼び出しを1回のMCPリクエストで実行する。LLMが複数のシーン操作を個別に呼ぶとラウンドトリップが増え遅延が大きくなるため、これらを単一リクエストにまとめる。

### 入力スキーマ

| パラメータ | 型 | 必須 | デフォルト | 説明 |
|---|---|---|---|---|
| `operations` | array | Yes | — | 実行するツール操作の配列（1〜50要素） |
| `operations[].tool_name` | string | Yes | — | 実行するツール名（MCP統一名を使用可） |
| `operations[].arguments` | object | No | `{}` | ツールの引数 |
| `stop_on_error` | boolean | No | `true` | Stop executing remaining operations on first error. |

### 出力仕様

| フィールド | 型 | 説明 |
|---|---|---|
| `success` | boolean | 全操作が成功した場合 `true` |
| `results` | array | 各操作の結果 |
| `results[].tool_name` | string | 実行されたツール名 |
| `results[].success` | boolean | 操作の成否 |
| `results[].result` | object? | 成功時のツール結果（失敗時は省略） |
| `results[].error` | string? | 失敗時のエラーメッセージ（成功時は省略） |
| `summary.total` | int | 総操作数 |
| `summary.succeeded` | int | 成功数 |
| `summary.failed` | int | 失敗数 |
| `summary.skipped` | int | スキップ数（`stop_on_error` による） |

### 動作ルール

- **逐次実行**: サーバーが各操作を通常のツール実行パスで逐次ディスパッチする。
  各操作のブリッジレベル安全処理（EnsureEditMode, 再接続ハンドリング等）が適用される。
- **インターリーブ**: 操作間に他リクエストがインターリーブする可能性がある（分離保証なし）。
  バッチの目的はラウンドトリップ削減であり分離保証ではない。
- **stop_on_error**: エラー発生後の操作は `"skipped"` エラーで記録
- **最大50操作**: `operations` 配列は最大50要素
- **禁止ツール**: `execute_batch` 自身のみバッチ内で使用不可（再帰防止、`ERR_INVALID_PARAMS`）
- **長時間実行ツール**: `run_tests`, `refresh_assets` 等もバッチに含められる。
  各操作は個別タイムアウトが適用される。
- **事前バリデーション**: 全操作を実行前に検証する（ツール名存在チェック、再帰チェック等）。
  不正な操作が含まれる場合、副作用なしで `ERR_INVALID_PARAMS` を返す。

### タイムアウト

| デフォルト | 最大 |
|---|---|
| 600000 ms | 2400000 ms |

---

## 12. 実装参照ファイル

### Server

| ファイル | 内容 |
|---|---|
| `Server/ToolCatalog.cs` | 全ツールのスキーマ・メタデータ定義 |
| `Server/ToolContracts.cs` | `ToolNames`, Request/Result records |
| `Server/Mcp.cs` | MCP tool 呼び出しのパースとルーティング |
| `Server/UnityBridge.cs` | Unity Plugin への通信 |

### Unity Plugin

| ファイル | 内容 |
|---|---|
| `UnityMCPPlugin/.../CommandExecutor.cs` | `read_console`, `get_editor_state`, `get_play_mode_state`, `clear_console`, `refresh_assets`, `control_play_mode`, `run_tests`, `execute_batch` の実行ロジック |
| `UnityMCPPlugin/.../Tools/CaptureScreenshotTool.cs` | スクリーンショットの撮影・保存実装 |
