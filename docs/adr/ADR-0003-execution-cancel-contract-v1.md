# ADR-0003: Tool実行モデル・キュー・キャンセル契約 v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
ADR-0001で `1 Editor = 1 MCP Server`、ADR-0002でWebSocket接続と再接続方針を確定した。  
本ADRでは、MCP Server内部の実行制御（キュー、並列度、キャンセル）をv1として定義する。

Unity Editor側はメインスレッド実行制約が強いため、実行契約を先に固定しないとtool実装ごとに挙動がばらつく。

## Decision
1. v1の実行モードは `sync` と `job` の2種類を持つ。
2. 各toolは `execution_mode` をメタデータで宣言する。
3. MCP Serverは単一Editor向けに1本の実行キューを持つ。
4. 実行中断は `cancel` を標準操作として定義する。
5. `client_request_id` はwire契約として維持し、v1では受理のみ行う（重複検出は将来版で有効化する）。

## Execution Modes
1. `sync`
   - 短時間処理向け
   - 1リクエスト1レスポンス
   - 既定タイムアウトを超過したら失敗
2. `job`
   - 長時間処理向け
   - `submit -> status/result -> cancel` の2段階操作
   - 切断復帰後も `job_id` で再取得可能

## Tool Metadata Contract (v1)
```json
{
  "name": "run_tests",
  "execution_mode": "job",
  "default_timeout_ms": 300000,
  "supports_cancel": true
}
```

`execution_mode` は `sync | job`。  
toolごとの選定は後続設計で行う。

## Request / Response Contract (v1)
### Common fields
1. `request_id`: MCP Server内部相関ID
2. `client_request_id`: 将来の重複抑止向けidempotencyキー（v1は受理のみ）
3. `tool_name`
4. `submitted_at`

### Sync
```json
{ "type":"execute", "request_id":"req-1", "tool_name":"read_console", "params":{} }
```
```json
{ "type":"result", "request_id":"req-1", "status":"ok", "result":{} }
```

### Job
```json
{ "type":"submit_job", "request_id":"req-2", "tool_name":"run_tests", "params":{} }
```
```json
{ "type":"submit_job_result", "request_id":"req-2", "status":"accepted", "job_id":"job-1001" }
```
```json
{ "type":"get_job_status", "job_id":"job-1001" }
```
```json
{ "type":"job_status", "job_id":"job-1001", "state":"queued|running|succeeded|failed|timeout|cancelled" }
```

## Queue and Concurrency Policy
1. キューはMCP Serverごとに1つ（Editor専用）。
2. 既定の同時実行数は `1`。
3. `job` と `sync` は同一キューで順序管理する。
4. `sync` のfast-path toolは将来の優先度拡張候補とするが、v1では優先度なし。
5. キュー上限を超えた場合は即時 `ERR_QUEUE_FULL` を返す。

## State Machine (Request)
1. `received`
2. `waiting_editor_ready`
3. `queued`
4. `running`
5. `succeeded | failed | timeout | cancelled`

`compiling/reloading` 状態では `waiting_editor_ready` に留める。
詳細なイベント遷移は ADR-0011 を正とする。

## Cancel Contract
1. `queued` のリクエスト
   - 即時キャンセル可能
   - 結果は `cancelled`
2. `running` かつ `supports_cancel=true`
   - `cancel` 呼び出しには即時 `cancel_requested` を返す
   - Unityへ `cancel` を送信し、最終状態は後続イベントで確定する
3. `running` かつ `supports_cancel=false`
   - `cancel` 呼び出しには即時 `cancel_requested` を返す
   - 実処理は継続し、終了時に通常結果または失敗を返す
4. `ERR_CANCEL_NOT_SUPPORTED`
   - v1では「cancelを受け付けない対象種別」に対してのみ返す。
   - `supports_cancel=false` の既知対象には使わず、`cancel_requested` を返す。

## Client Request ID Policy (v1)
1. `client_request_id` は受理・ログ相関に使ってよい。
2. v1では同値判定による実行抑止/結果再利用を行わない。
3. 変更系tool追加前に、server側dedupeを導入する。

## In-Memory Stores (Additions)
1. `request_store` (`request_id` 単位状態)
2. `job_store` (`job_id` 単位状態と最終結果)
3. `queue_store` (待機順序とキュー長)

## Error Codes (Additions)
1. `ERR_QUEUE_FULL`
2. `ERR_JOB_NOT_FOUND`
3. `ERR_CANCEL_NOT_SUPPORTED`
4. `ERR_CANCEL_REJECTED`
5. `ERR_EDITOR_NOT_READY` (待機上限超過時の未実行失敗)

## Consequences
1. toolごとの実行契約を統一できる。
2. 長時間処理を `job` で安全に扱える。
3. v1では切断復帰時の重複実行抑止は限定的で、運用で注意が必要。
4. 変更系tool追加前にdedupe有効化の設計判断が必要になる。

## Non-Goals (v1)
1. マルチキュー優先度スケジューラ
2. 分散永続キュー
3. メトリクス駆動の動的並列度制御
