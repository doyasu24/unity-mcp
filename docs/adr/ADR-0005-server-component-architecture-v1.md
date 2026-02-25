# ADR-0005: MCP Server内部コンポーネント構成 v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
ADR-0001〜0004で、以下を確定済み。
1. `1 Editor = 1 MCP Server`
2. Unity接続はWebSocketのみ
3. compile/reload状態通知を用いた待機復帰
4. tool実行は `sync/job` の2モード
5. `client_request_id` は将来の重複抑止向けに契約維持（v1は受理のみ）

本ADRは、MCP Serverの内部構成をv1として固定し、実装時の責務分離を明確化する。

## Decision
MCP Serverを以下のコンポーネントで構成する。

1. `ConfigLoader`
2. `McpEndpointAdapter`
3. `RequestValidator`
4. `RequestScheduler`
5. `ExecutionCoordinator`
6. `UnityBridgeClient` (WebSocket)
7. `ReconnectController`
8. `EditorStateTracker`
9. `JobManager`
10. `CancelManager`
11. `StateStores`
12. `Logger`

## Component Responsibilities
### 1) ConfigLoader
1. 起動設定を読み込む（port）。
2. 既定値補完と妥当性チェックを行う。

### 2) McpEndpointAdapter
1. MCPクライアントからのtool呼び出しを受け取る。
2. 内部共通リクエスト形式へ正規化する。
3. 返却形式をMCPレスポンスへ変換する。

### 3) RequestValidator
1. tool定義、必須引数、timeout範囲、`client_request_id` の形式を検証する。
2. 不正入力を早期に `ERR_INVALID_REQUEST` として返す。

### 4) RequestScheduler
1. Unity往復が必要な要求を単一キューへ投入する。
2. v1では `get_job_status/cancel_job` を含む全要求を同一キューで順序制御する。
3. 制御toolの処理自体は `ExecutionCoordinator` から `JobManager/CancelManager` へ委譲する。

### 5) ExecutionCoordinator
1. `sync/job` を判定して実行経路を選択する。
2. compile/reload時は `waiting_editor_ready` へ遷移させる。
3. timeout・cancel・再接続待機を統合制御する。

### 6) UnityBridgeClient (WebSocket)
1. Server側のWebSocket待受とPluginセッション管理を担当する。
2. `hello/editor_status/ping/pong/result/submit_job_result/job_status/cancel_result` を送受信する。
3. 受信メッセージを `ExecutionCoordinator` と `EditorStateTracker` へ通知する。

### 7) ReconnectController
1. 切断後の再接続待機ウィンドウを管理する。
2. Plugin側再接続（指数バックオフ）を前提に、Server側タイムアウト判定を行う。
3. 接続復帰時に保留要求の再開トリガーを発行する。

### 8) EditorStateTracker
1. `ready/compiling/reloading` を保持する。
2. 状態変化をイベントとして配信する。
3. 再接続後 `hello.state` で状態再同期する。

### 9) JobManager
1. `submit_job/get_job_status/cancel_job` を管理する。
2. `job_id` 発行と状態遷移を管理する。
3. 非永続方針のため、再起動時にjob状態を破棄する。

### 10) CancelManager
1. `supports_cancel=true` の要求に `cancel` を転送する。
2. `supports_cancel=false` の要求は `cancel_requested` を即時返す。

### 11) StateStores
1. `connection_store`
2. `request_store`
3. `job_store`
4. `queue_store`
5. `capability_store`
6. `version_store`

### 12) Logger
1. 相関ID (`request_id`, `client_request_id`, `job_id`) をログへ付与する。
2. version不一致、状態遷移、再接続、timeout、cancelを監査可能にする。

## Dependency Rules
1. `McpEndpointAdapter` は `RequestValidator` と `ExecutionCoordinator` にのみ依存する。
2. `ExecutionCoordinator` は `RequestScheduler`, `JobManager`, `CancelManager`, `EditorStateTracker`, `UnityBridgeClient` に依存する。
3. `UnityBridgeClient` は低レイヤ通信専任とし、業務ロジックを持たない。
4. `StateStores` への書き込みは各マネージャ経由とし、外部から直接更新しない。

## Runtime Flows
### A. sync tool (Unity往復あり)
1. `McpEndpointAdapter` 受信
2. `RequestValidator` 検証
3. `RequestScheduler` キュー投入
4. `ExecutionCoordinator` がUnity実行
5. `result` をMCPレスポンスに変換して返却

### B. job tool
1. `submit_job` 受信
2. `JobManager` が `job_id` 発行 (`queued`)
3. `RequestScheduler` 経由で実行 (`running`)
4. 完了で `succeeded/failed/cancelled`
5. `get_job_status` もv1では同一キュー上で順序制御する

### B-2. control tool (`get_job_status` / `cancel_job`)
1. `McpEndpointAdapter` 受信
2. `RequestValidator` 検証
3. `RequestScheduler` が同一キューへ投入
4. `ExecutionCoordinator` が `JobManager/CancelManager` へ委譲
5. `get_job_status`: `JobManager` から状態取得
6. `cancel_job`: `CancelManager` が対象状態を判定し、必要時のみUnity cancelを送信

### C. compile/reload中
1. `EditorStateTracker = compiling/reloading`
2. Unity往復要求は `waiting_editor_ready`
3. `ready` 復帰でキュー再開
4. `compile_grace_timeout` 超過で `ERR_COMPILE_TIMEOUT`

## Concurrency Model
1. Unity往復要求の同時実行数は `1`。
2. 全状態更新はNode.js単一イベントループで直列化する（v1でmutex分岐は採用しない）。
3. `job_store/request_store/queue_store` の更新はイベントループ内の単一遷移関数経由でのみ行う。

## Configuration Schema (v1)
```json
{
  "schema_version": 1,
  "unity_ws_port": 8091
}
```

## Operational Defaults (v1)
注記: `reconnect_*` はPlugin側再接続の既定値として扱い、Serverは待受を継続する。
注記: 既定値の正本は `docs/spec/IMPLEMENTATION_SPEC_V1.md` の定数テーブル（Section 10）とする。

1. `queue_max_size = 32`
2. `reconnect_initial_ms = 100`
3. `reconnect_multiplier = 1.7`
4. `reconnect_max_backoff_ms = 1200`
5. `reconnect_jitter_ratio = 0.1`
6. `heartbeat_interval_ms = 3000`
7. `heartbeat_timeout_ms = 4500`
8. `request_reconnect_wait_ms = 2500`
9. `compile_grace_timeout_ms = 60000`

## Failure Handling
1. Unity切断時: `ReconnectController` が切断状態へ遷移し、Plugin再接続待機を開始
2. 再接続不可: `ERR_RECONNECT_TIMEOUT`
3. compile/reload超過: `ERR_COMPILE_TIMEOUT`
4. キュー上限超過: `ERR_QUEUE_FULL`

## Error Propagation Contract
1. コンポーネント間エラーは「戻り値 (`Result<T, E>`)」で返し、想定内失敗に例外を使わない。
2. `RequestValidator` の検証失敗はキュー投入せず、その場で `McpEndpointAdapter` が `error` envelope化する。
3. `UnityBridgeClient` の送受信失敗は `ExecutionCoordinator` へ `bridge_disconnected|bridge_protocol_error` イベントとして通知する。
4. `ExecutionCoordinator` はイベントを状態遷移へ写像し、`ERR_UNITY_DISCONNECTED|ERR_INVALID_RESPONSE|ERR_RECONNECT_TIMEOUT` を決定する。
5. 予期しない例外のみ最上位で捕捉し、`ERR_UNITY_EXECUTION` または `ERR_INVALID_RESPONSE` に正規化して返す。

## Consequences
1. 実装責務が明確になり、並行実装しやすくなる。
2. ローカル用途での復帰速度と安全性のバランスが取れる。
3. デメリットとして、初期実装のコンポーネント数は増える。

## Non-Goals (v1)
1. 分散キュー/分散ストア
2. トランスポート多重化
3. メトリクス駆動の自動チューニング
