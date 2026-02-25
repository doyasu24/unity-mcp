# ADR-0013: エラー契約と再試行セマンティクス v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
ADR-0008でerror envelopeを定義し、ADR-0009/0010/0011で設定・起動・状態遷移を固定した。  
本ADRでは、`ERR_*` の返却条件、再試行可否、実行保証を定義する。  
ただし初期実装（v1.0）は最小契約を優先し、厳密マトリクスは後続版で有効化する。

## Decision
1. すべての失敗応答は `error` envelopeを使う（ADR-0008）。
2. v1.0で必須なのは `error.code` と `error.message` のみとする。
3. `retryable` と `details.execution_guarantee` はv1.0では任意（厳密運用はDeferred）。
4. 起動時設定エラー（ADR-0009）はhard failで起動停止する。

## Error Envelope (v1)
```json
{
  "type": "error",
  "protocol_version": 1,
  "request_id": "req-100",
  "error": {
    "code": "ERR_INVALID_PARAMS",
    "message": "tool_name is required",
    "retryable": false,
    "details": {
      "execution_guarantee": "not_executed"
    }
  }
}
```

## `execution_guarantee` Values
1. `not_executed`: Unityへ未送信を保証。
2. `unknown`: Unity送信済みの可能性があり、結果が不確定。
3. `completed_error`: 実行完了したがエラー結果で終了。

## Wire Error Matrix (Target / Deferred)
| code | retryable | execution_guarantee | Condition |
|---|---|---|---|
| `ERR_INVALID_REQUEST` | false | `not_executed` | 必須フィールド欠落、契約違反 |
| `ERR_INVALID_PARAMS` | false | `not_executed` | tool引数不正、timeout範囲外 |
| `ERR_UNKNOWN_COMMAND` | false | `not_executed` | 未知type/tool |
| `ERR_EDITOR_NOT_READY` | true | `not_executed` | `waiting_editor_ready` 待機上限超過 |
| `ERR_UNITY_DISCONNECTED` | true | `not_executed` | 実行前に切断検知し即失敗 |
| `ERR_RECONNECT_TIMEOUT` | true | `unknown` | 実行中の再接続待機が上限超過 |
| `ERR_COMPILE_TIMEOUT` | true | `not_executed` | compile/reload待機が上限超過 |
| `ERR_REQUEST_TIMEOUT` | true | `unknown` | 実行開始後に要求タイムアウト |
| `ERR_QUEUE_FULL` | true | `not_executed` | キュー上限超過 |
| `ERR_JOB_NOT_FOUND` | false | `not_executed` | 未知 `job_id` |
| `ERR_CANCEL_NOT_SUPPORTED` | false | `not_executed` | cancel非対応対象への直接cancelエラー |
| `ERR_CANCEL_REJECTED` | false | `unknown` | cancel拒否（対象は継続中の可能性） |
| `ERR_UNITY_EXECUTION` | depends | `completed_error` | Unity実行例外/失敗 |
| `ERR_INVALID_RESPONSE` | true | `unknown` | Unity応答不正/不整合 |
| `ERR_RECONFIG_IN_PROGRESS` | true | `not_executed` | port再設定排他中 |

上表は後続版で有効化する厳密契約のターゲットであり、v1.0では参考情報として扱う。  
`ERR_UNITY_EXECUTION.retryable` のtool単位固定もDeferredとする。
`ERR_CANCEL_NOT_SUPPORTED` は「cancel対象種別が契約外」の場合に限る。`supports_cancel=false` の既知対象は `cancel_requested` を返す。

## Startup/Config Error Matrix (Non-Wire)
| code | Action |
|---|---|
| `ERR_CONFIG_PARSE` | 起動停止（hard fail） |
| `ERR_CONFIG_VALIDATION` | 起動停止（hard fail） |
| `ERR_CONFIG_SCHEMA_VERSION` | 起動停止（hard fail） |

## Retry Guidance
1. `ERR_QUEUE_FULL` は指数バックオフ（例: 200ms開始、上限2000ms）で再試行する。
2. `ERR_EDITOR_NOT_READY|ERR_UNITY_DISCONNECTED|ERR_RECONNECT_TIMEOUT|ERR_REQUEST_TIMEOUT|ERR_INVALID_RESPONSE` は再試行候補とする。
3. v1ではserver側dedupeが未実装のため、同一 `client_request_id` でも重複実行が起こり得る。
4. 厳密な `retryable/execution_guarantee` 判定は後続版で有効化する。

## Logging Requirements
1. すべてのerrorログに `request_id` を付与する。
2. `client_request_id` がある場合は必ず併記する。
3. `execution_guarantee=unknown` を返す実装では、当該失敗を警告レベル以上で記録する。

## Consequences
1. v1.0は最小実装で先行できる。
2. 厳密マトリクスを後続で有効化する際に、エラー契約の段階強化が必要になる。

## Non-Goals (v1)
1. 例外スタックのwire転送標準化
2. 自動リトライ戦略の実装
3. 外部監視基盤への自動連携
