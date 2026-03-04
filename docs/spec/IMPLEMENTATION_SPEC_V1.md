# Unity MCP 実装仕様書

- Status: Draft
- Date: 2026-03-02

## 0. 目的
本仕様書は、Unity MCP のアーキテクチャと実装仕様を定める正本である。
ツール個別の入出力仕様は各ドメイン仕様書（EDITOR_TOOLS / SCENE_TOOLS / PREFAB_TOOLS / ASSET_TOOLS）を参照。

## 1. 実装スコープ
### 1.1 In Scope
1. `1 Editor = 1 MCP Server`
2. MCP endpointは `Streamable HTTP` (`/mcp`) で提供する
3. Unity接続はWebSocket (`/unity`) のみ
4. `waiting_editor` / `ready` / `compiling` / `reloading` を考慮した実行制御
5. `sync` 実行モデル
6. 27ツール（4ドメイン）
   - Editor（8）: エディタ状態・Play Mode・コンソール・テスト・スクリーンショット
   - Scene（10）: シーン管理・階層・コンポーネント・GameObject・検索
   - Prefab（5）: Prefab階層・コンポーネント・GameObject・検索
   - Asset（4）: アセット検索・情報取得・管理・マテリアル操作

### 1.2 Out of Scope
1. 単一ServerでのマルチEditor集約
2. リモート公開・認証
3. メトリクス/トレース
4. 動的モード切替・優先度キュー

## 2. 実装基盤
### 2.1 言語・ランタイム
1. Server: C# (.NET 8+)
2. MCP: C# MCP SDK
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
各コンポーネントの責務を以下に定義する。

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
   - 全要求を同一キューで順序制御する
6. `ExecutionCoordinator`
   - 実行分岐、待機、timeout を統合制御
7. `UnityBridgeClient`
   - `/unity` upgrade経由のPlugin WebSocketセッション管理
   - 送受信 (`hello/editor_status/execute/result`)
8. `ReconnectController`
   - 切断後の再接続待機ウィンドウ管理（Plugin再接続前提）
9. `EditorStateTracker`
   - `ready/compiling/reloading` と `seq` 管理
10. `Logger`
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

### 4.5 運用制約
1. マルチEditor運用時は各インスタンスで異なるportを手動設定する。
2. 既定値 `48091` は単一Editor運用向けであり、複数同時起動時は競合する。
3. `host=127.0.0.1` 固定のため、Server/Editorが同一ネットワーク名前空間にある前提。
4. Docker/WSL2等の分離名前空間はサポート対象外。
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
4. `entering_play_mode`

### 5.3 起動シーケンス
1. Server起動
2. CLI引数 `--port` 読込・検証
3. 単一HTTP listener (`127.0.0.1:{port}`) を起動
4. `/mcp` 公開と `/unity` upgrade受理を開始
5. Unity接続成立まで `waiting_editor`
6. `hello` 交換成功で `ready`

### 5.4 `waiting_editor` 受理ポリシー
1. 要求は待機理由に応じて次の上限まで待機する。
   - `reconnecting`: `request_reconnect_wait_ms` (45000ms)
   - `compiling|reloading|entering_play_mode`: `compile_grace_timeout_ms` (90000ms)
2. 復帰成功時のみ実行
3. 超過時は待機理由に応じたエラーを返す。
   - `reconnecting`: `ERR_EDITOR_NOT_READY`
   - `compiling|reloading`: `ERR_COMPILE_TIMEOUT`
4. 3の失敗は `retryable=true`, `execution_guarantee=not_executed`, `recovery_action=retry_allowed` を返す。
5. 復帰後の再開順序は FIFO

### 5.5 Shutdown
1. `stopping` 遷移後は新規要求を受理しない
2. `queued/waiting_editor_ready` は `ERR_EDITOR_NOT_READY` で終了
3. `running` は `ERR_RECONNECT_TIMEOUT` で終了する（`execution_guarantee=unknown`）
4. 専用shutdownメッセージは使わず、接続クローズで通知

### 5.6 Plugin Port Reconfigure時の要求扱い
1. `Unity MCP Settings` EditorWindow でのport再設定による切断は、Server側では通常の切断イベントとして扱う。
2. `running` 要求は `request_reconnect_wait_ms` 以内の再接続復帰で継続し、超過時は `ERR_RECONNECT_TIMEOUT` で終了する。
3. 2の失敗は `retryable=false`, `execution_guarantee=unknown`, `recovery_action=inspect_state_then_retry_if_needed` を返す。
4. Serverは `running` 要求を自動再送しない。
5. `queued/waiting_editor_ready` 要求は接続復帰後にFIFOで再開する。

## 6. 通信仕様
### 6.0 MCP endpoint（Claude/Codex <-> Server）
1. Transportは `Streamable HTTP` を採用する。
2. Endpointは `http://127.0.0.1:{port}/mcp` とする。
3. `/mcp` と `/unity` は同一listener上で提供する。
4. MCPクライアント接続に `stdio` は使わない。

### 6.1 共通Envelope（Unity Plugin <-> Server）
```json
{
  "type": "string"
}
```
`protocol_version` は `hello` メッセージにのみ含める。hello で互換性を確認した後、以降のメッセージには含めない。

### 6.2 利用メッセージ
1. `hello`
2. `capability`
3. `editor_status`
4. `execute` / `result`
5. `error`

### 6.3 接続シーケンス
1. Pluginが `ws://127.0.0.1:{port}/unity` へ接続
2. Plugin -> Server: `hello(plugin_version, editor_instance_id, plugin_session_id, connect_attempt_seq, state)`
3. Server -> Plugin: `hello(server_version, connection_id, editor_status_interval_ms)`
4. Server -> Plugin: `capability`
5. Plugin は `editor_status_interval_ms`（5秒）間隔で `editor_status` を定期送信する（暗黙のハートビート）
6. Server は `stale_connection_timeout_ms`（15秒）以内にアクティブソケットからメッセージを受信しなかった場合、接続を切断する
7. Serverは `hello` 受信完了まで接続を `pending` 扱いとし、既存 `active` セッションを切断しない。
8. 既存 `active` がある状態で同一 `editor_instance_id` の接続が `hello` を送信した場合、Serverは既存 `active` を置換して新規接続を採用する。
9. 既存 `active` がある状態で異なる `editor_instance_id` の接続が `hello` を送信した場合、Serverは既存セッションを維持し、新規接続を拒否する。
10. 9の拒否時、Serverは `error` (`code=ERR_INVALID_REQUEST`, `message="another Unity websocket session is already active"`) を返した後、新規接続をcloseする。
11. 9を受信したPlugin（2つ目のEditor）は接続失敗として扱い、ユーザー向けに次の案内ログを出す。
   - `Connection rejected: multiple Unity Editors are trying to use the same MCP server. Close one Editor, or see README > Using Multiple Unity Editors.`
12. 11の案内ログは同一競合状態の間は重複出力しない。`hello` 成功後に再び競合が起きた場合は再出力してよい。

### 6.4 フィールド命名
1. 相関キーは `request_id`
2. `client_request_id` はidempotencyキーとして予約する（現在は受理のみで重複判定には使わない）
3. 未知フィールドは無視

### 6.5 `result` と `error` の使い分け
1. `result(status=error)` は「実行開始後のtool実行エラー」に使用
2. `error` は「バリデーション/ルーティング/プロトコル」失敗に使用

### 6.6 メッセージサイズ上限
1. 単一メッセージ上限は `1MB`
2. 受信側は上限超過を `ERR_INVALID_REQUEST` として扱い、必要に応じて接続を閉じる
3. 受信応答が上限超過だった場合は `ERR_INVALID_RESPONSE` として扱う

### 6.7 capability tool metadata
1. `capability.tools[]` は少なくとも `name/default_timeout_ms/max_timeout_ms/requires_client_request_id` を含む。

## 7. 実行モデル
### 7.1 Request状態
1. `received`
2. `waiting_editor_ready`
3. `queued`
4. `running`
5. `succeeded|failed|timeout|cancelled`

### 7.2 並列実行
1. Unity往復要求の同時実行数は `1`
2. キュー上限は `32`
3. 全要求を同一キューで処理する
4. 状態更新は単一直列化コンテキストで処理し、mutex分岐は採用しない

### 7.3 raceルール
1. 終端状態はCASで1回のみ確定
2. 同一 `request_id` の後着 `result/error` はログのみで破棄する

## 8. エラー契約
### 8.1 error envelope
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
1. `code`, `message`, `retryable`, `details.execution_guarantee`, `details.recovery_action` は必須。
2. `execution_guarantee` は `not_executed|unknown` のいずれかを返す。

### 8.2 主要エラー
1. 入力/契約: `ERR_INVALID_REQUEST`, `ERR_INVALID_PARAMS`, `ERR_UNKNOWN_COMMAND`
2. 接続/待機: `ERR_EDITOR_NOT_READY`, `ERR_UNITY_DISCONNECTED`, `ERR_RECONNECT_TIMEOUT`, `ERR_COMPILE_TIMEOUT`
3. 実行: `ERR_REQUEST_TIMEOUT`, `ERR_UNITY_EXECUTION`, `ERR_INVALID_RESPONSE`
4. キュー: `ERR_QUEUE_FULL`
5. 再設定: `ERR_RECONFIG_IN_PROGRESS`

### 8.3 再試行ルール
1. `ERR_QUEUE_FULL` は指数バックオフ（例: 200ms開始、上限2000ms）で再試行する。
2. `ERR_EDITOR_NOT_READY|ERR_COMPILE_TIMEOUT|ERR_UNITY_DISCONNECTED` は再試行候補とする（`retryable=true`）。
3. `ERR_RECONNECT_TIMEOUT|ERR_REQUEST_TIMEOUT` は即時再試行を推奨しない（`retryable=false`）。
4. 3のケースは `execution_guarantee=unknown` で返し、状態確認後のみ再試行する。
5. サーバー側dedupeは未実装のため、同一 `client_request_id` でも重複実行が起こり得る。

## 9. 運用既定値（内部固定）
注記: `reconnect_*` はPlugin側再接続ロジックの既定値、Serverは待受を継続する。

1. `reconnect_initial_ms = 200`
2. `reconnect_multiplier = 1.8`
3. `reconnect_max_backoff_ms = 5000`
4. `reconnect_jitter_ratio = 0.2`
5. `stale_connection_timeout_ms = 15000`
6. `editor_status_interval_ms = 5000`
7. `request_reconnect_wait_ms = 45000`
8. `compile_grace_timeout_ms = 90000`
9. `queue_max_size = 32`
10. `max_message_bytes = 1048576`
11. `mcp_http_path = /mcp`
13. `unity_ws_path = /unity`

## 10. ログ要件
1. すべての要求に `request_id`
2. 存在時 `client_request_id`
3. 接続ログには `connection_id`, `editor_instance_id`, `plugin_session_id`, `connect_attempt_seq` を付与する。
4. 主要状態遷移ログ
   - `booting -> waiting_editor -> ready`
5. `ERR_*` と主要コンテキスト（state/tool/request_id）を必ず記録する
6. 複数Editor競合で接続拒否されたPluginは、ユーザー向けエラーログに「片方のEditorを閉じる」または「README > Using Multiple Unity Editors」を必ず含める。

## 11. テスト要件
### 11.1 Unit
1. 設定検証（正常/異常/hard fail）
2. request state machine遷移
3. error mapping（最低限 `code/message`）

### 11.2 Integration (Mock Unity可)
1. `waiting_editor` 待機成功/失敗
2. `read_console` 正常取得
3. `get_editor_state` 状態反映
4. `run_tests` 同期実行で結果が直接返る
5. compile/reload中の待機復帰
7. `POST /mcp` で initialize/listTools/callTool が成功
8. 同一port上で `/mcp` と `/unity` が共存動作
9. 既存 `active` セッション中に2つ目のEditorが `hello` を送信した場合、2つ目は拒否され、Pluginのユーザー向け案内ログが1回だけ出る。

## 12. 実装チェックリスト
1. `ERR_*` と `code/message` の返却契約が一致している
4. 設定不正時に必ず起動停止する
5. `http://127.0.0.1:{port}/mcp` でMCP登録・呼び出し可能
6. `ws://127.0.0.1:{port}/unity` でUnity接続可能

## 13. 未実装事項
1. `client_request_id` のサーバー側dedupe（未完了重複拒否、完了結果再利用、TTL管理）。
2. dedupe有効化時の `ERR_DUPLICATE_REQUEST` 復帰と `retryable/execution_guarantee` 対応表の更新。
3. 厳密エラー契約（tool単位 `execution_guarantee` マトリクス）。
