# Unity MCP 実装仕様書 v1.0（初期実装）

- Status: Draft (Implementation Baseline)
- Date: 2026-02-25
- Source: ADR-0001 〜 ADR-0013

## 0. 目的
本仕様書は、ADR群の決定事項を実装向けに統合した正本である。  
初期実装では「最小機能で確実に動く」ことを優先し、`read_console` / `get_editor_state` / `run_tests(job)` 系を対象とする。

### 0.1 ADR優先順位（競合時）
1. Wire Protocol: ADR-0008
2. State Machine: ADR-0011
3. Tool Scope: ADR-0012
4. Error Contract: ADR-0013
5. Startup Acceptance: ADR-0010
6. Configuration Lifecycle: ADR-0009
7. その他（0001〜0007）

## 1. 実装スコープ
### 1.1 In Scope (v1.0)
1. `1 Editor = 1 MCP Server`
2. MCP endpointは `Streamable HTTP` (`/mcp`) で提供する
3. Unity接続はWebSocket (`/unity`) のみ
4. `waiting_editor` / `ready` / `compiling` / `reloading` を考慮した実行制御
5. `sync` + `job` 実行モデル
6. 初期公開tool（5個）
   - `read_console` (sync)
   - `get_editor_state` (sync)
   - `run_tests` (job, supports_cancel=true)
   - `get_job_status` (sync, control)
   - `cancel_job` (sync, control)

### 1.2 Out of Scope (v1.0)
1. 単一ServerでのマルチEditor集約
2. リモート公開・認証
3. メトリクス/トレース
4. 動的モード切替・優先度キュー
5. 初期5tool以外の公開

## 2. 実装基盤
### 2.1 言語・ランタイム（実装方針）
1. Server: C# (.NET 8+)
2. MCP: C# MCP SDK（採用版）
3. MCP endpoint transport: `Streamable HTTP`
4. Unity橋渡し: ASP.NET Core WebSocket
5. バリデーション: C#入力モデル検証（DataAnnotations等）

### 2.2 プロセストポロジ
1. `MCP Client <-> MCP Server (/mcp over HTTP)`
2. `Unity Editor <-> MCP Server (/unity over WebSocket)`
3. 1プロセス1Editor固定バインド（port一致）
4. 単一HTTP listener上で `/mcp` と `/unity` を同居させる
5. Unity接続方向は「Plugin(client) -> MCP Server(listener)」で固定

## 3. コンポーネント責務（Server）
ADR-0005を実装単位に落とし込む。

1. `ConfigLoader`
   - CLI `--port` を読込・検証（`/mcp` と `/unity` の共通listen port）
2. `HttpHost`
   - `127.0.0.1:{port}` でHTTP listenerを起動
   - `/mcp` ルートと `/unity` upgrade をルーティング
3. `McpEndpointAdapter`
   - `/mcp` 経由のMCP tool呼び出しを内部リクエストへ変換
4. `RequestValidator`
   - tool存在・引数・timeout上限・`client_request_id` の形式要件を検証
5. `RequestScheduler`
   - Unity往復要求を単一FIFOキューへ投入
   - v1では `get_job_status/cancel_job` を含む全要求を同一キューで順序制御する
6. `ExecutionCoordinator`
   - `sync/job` 実行分岐、待機、timeout、cancelを統合制御
   - 制御toolは `JobManager/CancelManager` へ委譲
7. `UnityBridgeClient`
   - `/unity` upgrade経由のPlugin WebSocketセッション管理
   - 送受信 (`hello/editor_status/ping/pong/result/submit_job_result/job_status/cancel_result`)
8. `ReconnectController`
   - 切断後の再接続待機ウィンドウ管理（Plugin再接続前提）
9. `EditorStateTracker`
   - `ready/compiling/reloading` と `seq` 管理
10. `JobManager`
   - `job_id` 発行、job状態管理（非永続）
11. `CancelManager`
   - cancel転送と結果整形
12. `Logger`
   - `request_id/client_request_id/job_id` 相関ログ

### 3.1 コンポーネント間エラー伝播
1. 想定内失敗は `Result<T, E>` で返し、例外は予期しない障害に限定する。
2. `RequestValidator` エラーはキュー投入せず、`McpEndpointAdapter` が即時 `error` envelope化する。
3. `UnityBridgeClient` の送受信失敗は `bridge_disconnected|bridge_protocol_error` イベントで `ExecutionCoordinator` に通知する。
4. `ExecutionCoordinator` はイベントを状態遷移へ写像し、`ERR_UNITY_DISCONNECTED|ERR_INVALID_RESPONSE|ERR_RECONNECT_TIMEOUT` を決定する。
5. 予期しない例外は最上位で捕捉し、`ERR_UNITY_EXECUTION` または `ERR_INVALID_RESPONSE` に正規化する。

## 4. 設定仕様
### 4.1 ファイル配置（Pluginはproject-scope）
1. Plugin: `ProjectSettings/UnityMcpPluginSettings.asset`（`ScriptableSingleton`）
2. Serverは設定ファイルを使わず、起動時引数で指定する。

### 4.2 スキーマ
Server:
1. 起動時引数 `--port` で指定する（任意）。
2. 未指定時は `48091` を使う。

Plugin:
```csharp
[FilePath("ProjectSettings/UnityMcpPluginSettings.asset", FilePathAttribute.Location.ProjectFolder)]
internal sealed class UnityMcpPluginSettings : ScriptableSingleton<UnityMcpPluginSettings>
{
    public int schemaVersion = 1;
    public int port = 48091;
}
```
Note:
1. Plugin `port` は Server `port` と同値に設定する。
2. Plugin設定は `Unity MCP Settings` EditorWindow で変更する。
3. Plugin設定は project-scope として扱い、設定ファイル差分をコミット対象とする。
4. 互換運用は行わず、`UserSettings` 旧設定の読み込み/移行はしない。

### 4.3 優先順位
Server:
1. CLI `--port`
2. 既定値 `48091`

Plugin:
1. EditorWindowで適用済みの `ScriptableSingleton` メモリ値
2. `ProjectSettings/UnityMcpPluginSettings.asset`
3. 既定値 `48091`

### 4.4 検証失敗時
1. Server: `--port` が不正な場合は `ERR_CONFIG_VALIDATION` で起動停止。
2. Plugin: 設定アセットの読込/検証失敗（`schemaVersion`/`port`）時は初期化停止。

### 4.5 運用制約（v1）
1. マルチEditor運用時は各インスタンスで異なるportを手動設定する。
2. 既定値 `48091` は単一Editor運用向けであり、複数同時起動時は競合する。
3. `host=127.0.0.1` 固定のため、Server/Editorが同一ネットワーク名前空間にある前提。
4. Docker/WSL2等の分離名前空間はv1標準サポート対象外。
5. `/mcp` と `/unity` は同一portで提供し、pathで識別する。
6. Pluginのport変更は `ProjectSettings/UnityMcpPluginSettings.asset` に保存し、チーム共有する。

## 5. ライフサイクルと状態
### 5.1 Server状態
1. `booting`
2. `waiting_editor`
3. `ready`
4. `stopping`
5. `stopped`

### 5.2 Editor状態（接続後）
1. `ready`
2. `compiling`
3. `reloading`

### 5.3 起動シーケンス
1. Server起動
2. CLI引数 `--port` 読込・検証
3. 単一HTTP listener (`127.0.0.1:{port}`) を起動
4. `/mcp` 公開と `/unity` upgrade受理を開始
5. Unity接続成立まで `waiting_editor`
6. `hello` 交換成功で `ready`

### 5.4 `waiting_editor` 受理ポリシー
1. `sync/job` は `request_reconnect_wait_ms` (2500ms) まで待機
2. 復帰成功時のみ実行
3. 超過時 `ERR_EDITOR_NOT_READY` + `not_executed` 保証
4. `submit_job` 超過時は `job_id` 未発行
5. 待機中 cancel は即時 `cancelled`
6. 復帰後の再開順序は FIFO

### 5.5 Shutdown
1. `stopping` 遷移後は新規要求を受理しない
2. `queued/waiting_editor_ready` は `ERR_EDITOR_NOT_READY` で終了
3. `running` は `ERR_RECONNECT_TIMEOUT` で終了する（`execution_guarantee` を返す実装では `unknown`）
4. v1では専用shutdownメッセージは使わず、接続クローズで通知

### 5.6 Plugin Port Reconfigure時の要求扱い
1. `Unity MCP Settings` EditorWindow でのport再設定による切断は、Server側では通常の切断イベントとして扱う。
2. `running` 要求は `request_reconnect_wait_ms` 以内の再接続復帰で継続し、超過時は `ERR_RECONNECT_TIMEOUT` で終了する。
3. 上記失敗時に `execution_guarantee` を返す実装では `unknown` を設定する。
4. `queued/waiting_editor_ready` 要求は接続復帰後にFIFOで再開する。

## 6. 通信仕様
### 6.0 MCP endpoint（Claude/Codex <-> Server）
1. Transportは `Streamable HTTP` を採用する。
2. Endpointは `http://127.0.0.1:{port}/mcp` とする。
3. `/mcp` と `/unity` は同一listener上で提供する。
4. v1運用モードではMCPクライアント接続に `stdio` を使わない。

### 6.1 共通Envelope（Unity Plugin <-> Server）
```json
{
  "type": "string",
  "protocol_version": 1,
  "timestamp": "RFC3339 (optional)"
}
```

### 6.2 利用メッセージ（v1.0実装で必須）
1. `hello`
2. `capability`
3. `editor_status`
4. `ping` / `pong`
5. `execute` / `result`
6. `submit_job` / `submit_job_result`
7. `get_job_status` / `job_status`
8. `cancel` / `cancel_result`
9. `error`

### 6.3 接続シーケンス
1. Pluginが `ws://127.0.0.1:{port}/unity` へ接続
2. Plugin -> Server: `hello(plugin_version, state)`
3. Server -> Plugin: `hello(server_version)`
4. Server -> Plugin: `capability`
5. heartbeatは Server `ping` -> Plugin `pong`
6. Serverは `hello` 受信完了まで接続を `pending` 扱いとし、既存 `active` セッションを切断しない。
7. 既存 `active` がある状態で別接続が `hello` を送信した場合、Serverは既存セッションを維持し、新規接続を拒否する。
8. 7の拒否時、Serverは `error` (`code=ERR_INVALID_REQUEST`, `message=\"another Unity websocket session is already active\"`) を返した後、新規接続をcloseする。
9. 8を受信したPlugin（2つ目のEditor）は接続失敗として扱い、ユーザー向けに次の案内ログを出す。
   - `Connection rejected: multiple Unity Editors are trying to use the same MCP server. Close one Editor, or see README > Using Multiple Unity Editors.`
10. 9の案内ログは同一競合状態の間は重複出力しない。`hello` 成功後に再び競合が起きた場合は再出力してよい。

### 6.4 フィールド命名
1. 相関キーは `request_id`
2. `client_request_id` は将来の重複抑止向けidempotencyキーとして予約する（v1は受理のみで重複判定には使わない）
3. job相関は `job_id`
4. 未知フィールドは無視

### 6.5 `result` と `error` の使い分け
1. `result(status=error)` は「実行開始後のtool実行エラー」に使用
2. `error` は「バリデーション/ルーティング/プロトコル」失敗に使用

### 6.6 メッセージサイズ上限
1. 単一メッセージ上限は `1MB`
2. 受信側は上限超過を `ERR_INVALID_REQUEST` として扱い、必要に応じて接続を閉じる
3. 受信応答が上限超過だった場合は `ERR_INVALID_RESPONSE` として扱う

### 6.7 capability tool metadata
1. `capability.tools[]` は少なくとも `name/execution_mode/supports_cancel/default_timeout_ms/max_timeout_ms/requires_client_request_id` を含む。
2. `execution_error_retryable` は予約フィールドとして任意送信可（v1では判定に使わない）。

## 7. 実行モデル
### 7.1 Request状態
1. `received`
2. `waiting_editor_ready`
3. `queued`
4. `running`
5. `succeeded|failed|timeout|cancelled`

### 7.2 Job状態
1. `queued`
2. `running`
3. `succeeded|failed|timeout|cancelled`

### 7.3 並列実行
1. Unity往復要求の同時実行数は `1`
2. キュー上限は `32`
3. v1では `get_job_status/cancel_job` を含む全要求を同一キューで処理する
4. 状態更新は単一直列化コンテキストで処理し、mutex分岐は採用しない

### 7.4 raceルール
1. 終端状態はCASで1回のみ確定
2. `running` 中の `cancel_job` は常に `cancel_requested` を返す（`supports_cancel=true` の場合はUnityへ `cancel` を転送）
3. 終端済み対象への `cancel_job` は `rejected` を返す
4. 同一 `request_id` の後着 `result/error` はログのみで破棄する

## 8. 初期公開tool仕様（MCP面）
### 8.0 分類ルール補足
1. `sync/job` 分類の5秒基準は「対話的操作として待機可能な上限」の運用ルール。
2. `sync_default_timeout_ms=30000` は一時遅延吸収の上限であり、通常期待時間を30秒にする意味ではない。
3. 5秒基準を恒常的に超えるtoolは `job` へ昇格する。

### 8.0.1 厳密エラー契約（Deferred）
1. `execution_error_retryable` を用いた tool単位 `retryable` 決定は Deferred とする。
2. v1では `ERR_UNITY_EXECUTION` の `retryable` は既定 `false` として扱ってよい。

## 8.1 `get_editor_state` (sync)
### Input
```json
{}
```
### Output
```json
{
  "server_state": "waiting_editor|ready|stopping|stopped",
  "editor_state": "unknown|ready|compiling|reloading",
  "connected": true,
  "last_editor_status_seq": 42
}
```
Note:
1. `booting` はMCP endpoint公開前のため、通常このtoolでは観測されない。

## 8.2 `read_console` (sync)
### Input
```json
{
  "max_entries": 200
}
```
Rules:
1. `max_entries` 省略時は `200`
2. `1..2000` の範囲外は `ERR_INVALID_PARAMS`

### Output
```json
{
  "entries": [
    {
      "type": "log|warning|error|assert|exception",
      "message": "...",
      "stack_trace": "..."
    }
  ],
  "count": 123,
  "truncated": false
}
```

## 8.3 `run_tests` (job)
### Input
```json
{
  "mode": "all|edit|play",
  "filter": "optional string"
}
```
Rules:
1. `mode` 省略時は `all`
2. `run_tests` は `supports_cancel=true`
3. 受理時に `job_id` を発行

### submit response
```json
{
  "job_id": "job-...",
  "state": "queued"
}
```

### terminal result (in job status)
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

## 8.4 `get_job_status` (sync control)
### Input
```json
{
  "job_id": "job-..."
}
```
### Output
```json
{
  "job_id": "job-...",
  "state": "queued|running|succeeded|failed|timeout|cancelled",
  "progress": null,
  "result": {}
}
```
Rules:
1. 未知 `job_id` は `ERR_JOB_NOT_FOUND`
2. 副作用なし・再試行可
3. v1では進捗pushメッセージを定義しないため、`progress` は `null` でもよい

## 8.5 `cancel_job` (sync control)
### Input
```json
{
  "job_id": "job-..."
}
```
### Output
```json
{
  "job_id": "job-...",
  "status": "cancelled|cancel_requested|rejected"
}
```

## 9. エラー契約
### 9.1 error envelope
```json
{
  "type": "error",
  "protocol_version": 1,
  "request_id": "req-...",
  "error": {
    "code": "ERR_*",
    "message": "...",
    "retryable": false,
    "details": {}
  }
}
```
Rules:
1. v1で必須なのは `code` と `message` のみ。
2. `retryable` と `details.execution_guarantee` は任意（厳密運用はDeferred）。

### 9.2 主要エラー
1. 入力/契約: `ERR_INVALID_REQUEST`, `ERR_INVALID_PARAMS`, `ERR_UNKNOWN_COMMAND`
2. 接続/待機: `ERR_EDITOR_NOT_READY`, `ERR_UNITY_DISCONNECTED`, `ERR_RECONNECT_TIMEOUT`, `ERR_COMPILE_TIMEOUT`
3. 実行: `ERR_REQUEST_TIMEOUT`, `ERR_UNITY_EXECUTION`, `ERR_INVALID_RESPONSE`
4. キュー: `ERR_QUEUE_FULL`
5. job/cancel: `ERR_JOB_NOT_FOUND`, `ERR_CANCEL_NOT_SUPPORTED`, `ERR_CANCEL_REJECTED`
6. 再設定: `ERR_RECONFIG_IN_PROGRESS`

補足:
1. `ERR_CANCEL_NOT_SUPPORTED` は対象種別がcancel契約外の場合のみ使用する。
2. `supports_cancel=false` の既知対象は `cancel_requested` を返す。

### 9.3 再試行ルール
1. `ERR_QUEUE_FULL` は指数バックオフ（例: 200ms開始、上限2000ms）で再試行する。
2. `ERR_EDITOR_NOT_READY|ERR_UNITY_DISCONNECTED|ERR_RECONNECT_TIMEOUT|ERR_REQUEST_TIMEOUT|ERR_INVALID_RESPONSE` は再試行候補とする。
3. それ以外は原則再試行しない（運用判断で上書き可）。
4. v1ではサーバー側dedupeを有効化しないため、同一 `client_request_id` でも重複実行が起こり得る。

## 10. 運用既定値（内部固定）
注記: `reconnect_*` はPlugin側再接続ロジックの既定値、Serverは待受を継続する。

1. `reconnect_initial_ms = 100`
2. `reconnect_multiplier = 1.7`
3. `reconnect_max_backoff_ms = 1200`
4. `reconnect_jitter_ratio = 0.1`
5. `heartbeat_interval_ms = 3000`
6. `heartbeat_timeout_ms = 4500`
7. `request_reconnect_wait_ms = 2500`
8. `compile_grace_timeout_ms = 60000`
9. `queue_max_size = 32`
10. `max_message_bytes = 1048576`
11. `mcp_http_path = /mcp`
12. `unity_ws_path = /unity`

## 11. ログ要件
1. すべての要求に `request_id`
2. 存在時 `client_request_id` / `job_id`
3. 主要状態遷移ログ
   - `booting -> waiting_editor -> ready`
4. `ERR_*` と主要コンテキスト（state/tool/request_id）を必ず記録する
5. 複数Editor競合で接続拒否されたPluginは、ユーザー向けエラーログに「片方のEditorを閉じる」または「README > Using Multiple Unity Editors」を必ず含める。

## 12. テスト要件（最小）
### 12.1 Unit
1. 設定検証（正常/異常/hard fail）
2. request state machine遷移
3. error mapping（最低限 `code/message`）

### 12.2 Integration (Mock Unity可)
1. `waiting_editor` 待機成功/失敗
2. `read_console` 正常取得
3. `get_editor_state` 状態反映
4. `run_tests` submit->status->完了
5. `cancel_job` (`cancelled|cancel_requested|rejected`)
6. compile/reload中の待機復帰
7. `POST /mcp` で initialize/listTools/callTool が成功
8. 同一port上で `/mcp` と `/unity` が共存動作
9. 既存 `active` セッション中に2つ目のEditorが `hello` を送信した場合、2つ目は拒否され、Pluginのユーザー向け案内ログが1回だけ出る。

## 13. 実装フェーズ分割
1. Phase 1: Server skeleton + single HTTP host (`/mcp` + `/unity`) + config + state machine + wire
2. Phase 2: `get_editor_state` / `read_console`
3. Phase 3: `run_tests` + `get_job_status` + `cancel_job`
4. Phase 4 (Deferred): error contract strict化 + test整備

## 14. 実装時チェックリスト
1. capability公開が初期5toolのみになっている
2. `waiting_editor` 超過時に `job_id` を発行していない
3. `cancel` 競合で終端状態が二重確定しない
4. `ERR_*` と `code/message` の返却契約が一致している
5. 設定不正時に必ず起動停止する
6. `http://127.0.0.1:{port}/mcp` でMCP登録・呼び出し可能
7. `ws://127.0.0.1:{port}/unity` でUnity接続可能

## 15. 実装TODO（Deferred）
1. `client_request_id` のサーバー側dedupeを有効化する（未完了重複拒否、完了結果再利用、TTL管理）。
2. 変更系toolを追加する前に、`requires_client_request_id=true` の運用ルールと検証を有効化する。
3. dedupe有効化時に `ERR_DUPLICATE_REQUEST` を復帰し、`retryable/execution_guarantee` の対応表を更新する。
4. dedupe有効化時に Unit/Integration テストを追加する（重複拒否、TTL期限切れ、再試行時の重複抑止）。
5. `get_job_status/cancel_job` のキューバイパス最適化を再導入する（必要時のみ）。
6. 厳密エラー契約（tool単位 `execution_error_retryable` / `execution_guarantee` マトリクス）を有効化する。
