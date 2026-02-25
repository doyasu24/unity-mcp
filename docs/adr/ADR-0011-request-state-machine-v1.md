# ADR-0011: Request/Job状態遷移とCancel競合ルール v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
ADR-0003で実行モード契約（`sync/job`）を定義し、ADR-0010で起動時の受理方針（`waiting_editor`）を定義した。  
本ADRでは、`execute` / `submit_job` / `cancel` の状態遷移をイベント単位で固定し、
競合時の挙動をdeterministicにする。

## Decision
1. リクエスト状態は単一ステートマシンで管理し、終端状態は1回だけ確定する。
2. `waiting_editor_ready` は要求単位の待機状態とし、理由（`disconnected|compiling|reloading`）を保持する。
3. `waiting_editor_ready` 待機上限は `request_reconnect_wait_ms` を使う（ADR-0010）。
4. `submit_job` は受理成功時のみ `job_id` を発行する。
5. `cancel` は対象状態により `cancelled | cancel_requested | rejected` を返す（ADR-0008）。
6. `running` への `cancel` は常に `cancel_requested` を返し、終端確定は後続イベントで行う。

## Terminology
1. `server_state.waiting_editor`: Server接続未確立（ADR-0010）
2. `request_state.waiting_editor_ready`: 当該要求がEditor ready待機中
3. `terminal_state`: `succeeded|failed|timeout|cancelled`

## Request State Set
1. `received`
2. `waiting_editor_ready`
3. `queued`
4. `running`
5. `succeeded`
6. `failed`
7. `timeout`
8. `cancelled`

## Job State Set
1. `queued`
2. `running`
3. `succeeded`
4. `failed`
5. `timeout`
6. `cancelled`

## Transition Table: `execute` (sync)
| Current | Event | Guard | Next | Action |
|---|---|---|---|---|
| `received` | validate_ok | Editor ready | `queued` | キュー投入 |
| `received` | validate_ok | Editor not ready | `waiting_editor_ready` | 待機開始時刻・期限を設定 |
| `received` | validate_error | - | `failed` | `ERR_INVALID_REQUEST/ERR_INVALID_PARAMS` |
| `received` | queue_full | - | `failed` | `ERR_QUEUE_FULL` |
| `waiting_editor_ready` | editor_ready | 期限内 | `queued` | FIFO順でキュー投入 |
| `waiting_editor_ready` | wait_timeout | - | `failed` | `ERR_EDITOR_NOT_READY`（未実行保証） |
| `waiting_editor_ready` | cancel | - | `cancelled` | 即時 `cancelled` |
| `queued` | dequeue | - | `running` | Unityへ `execute` 送信 |
| `queued` | cancel | - | `cancelled` | キュー除外 |
| `running` | unity_result_ok | - | `succeeded` | 結果返却 |
| `running` | unity_result_error | - | `failed` | Unityエラーを返却 |
| `running` | request_timeout | - | `timeout` | `ERR_REQUEST_TIMEOUT` |
| `running` | reconnect_timeout | - | `failed` | `ERR_RECONNECT_TIMEOUT` |
| `running` | cancel | supports_cancel=true | `running` | cancel呼び出し側へ `cancel_requested`（Unityへ `cancel` 送信） |
| `running` | cancel | supports_cancel=false | `running` | cancel呼び出し側へ `cancel_requested` |

## Transition Table: `submit_job` (submission request)
注記: ここでの `succeeded` は「submitリクエストの成功（accepted）」を意味し、job本体の成功ではない。

| Current | Event | Guard | Next | Action |
|---|---|---|---|---|
| `received` | validate_ok | Editor ready | `succeeded` | `job_id` 発行 + `job_state=queued` + `accepted` 応答 |
| `received` | validate_ok | Editor not ready | `waiting_editor_ready` | 待機開始 |
| `received` | validate_error | - | `failed` | `ERR_INVALID_REQUEST/ERR_INVALID_PARAMS` |
| `waiting_editor_ready` | editor_ready | 期限内 | `succeeded` | `job_id` 発行 + `accepted` 応答 |
| `waiting_editor_ready` | wait_timeout | - | `failed` | `ERR_EDITOR_NOT_READY`（`job_id` 未発行） |
| `waiting_editor_ready` | cancel | - | `cancelled` | 即時 `cancelled`（`job_id` 未発行） |

## Transition Table: Job runtime
| Current | Event | Guard | Next | Action |
|---|---|---|---|---|
| `queued` | dequeue | - | `running` | Unityへ実行送信 |
| `queued` | cancel | - | `cancelled` | 実行前取消 |
| `running` | unity_result_ok | - | `succeeded` | 結果保持 |
| `running` | unity_result_error | - | `failed` | 失敗保持 |
| `running` | request_timeout | - | `timeout` | `ERR_REQUEST_TIMEOUT` |
| `running` | reconnect_timeout | - | `failed` | `ERR_RECONNECT_TIMEOUT` |
| `running` | cancel | supports_cancel=true | `running` | cancel呼び出し側へ `cancel_requested`（Unityへ `cancel` 送信） |
| `running` | cancel | supports_cancel=false | `running` | cancel呼び出し側へ `cancel_requested` |

## Transition Table: `cancel`
| Target Type | Target State | Result |
|---|---|---|
| request | `waiting_editor_ready` | `cancelled` |
| request | `queued` | `cancelled` |
| request | `running` and `supports_cancel=true` | `cancel_requested` |
| request | `running` and `supports_cancel=false` | `cancel_requested` |
| request | terminal | `rejected` |
| job | `queued` | `cancelled` |
| job | `running` and `supports_cancel=true` | `cancel_requested` |
| job | `running` and `supports_cancel=false` | `cancel_requested` |
| job | terminal | `rejected` |
| job | not found | `ERR_JOB_NOT_FOUND` |

## Race Handling Rules
1. 同一 `request_id/job_id` の終端確定はCompare-And-Setで1回のみ許可する。
2. `running` への `cancel` は終端を即時確定しない。呼び出し結果は常に `cancel_requested` とする。
3. 終端後に到着した `cancel` は常に `rejected`。
4. 同一 `request_id` の重複 `result/error` は最初の1件のみ有効（ADR-0008）。
5. `cancel_job` 呼び出しには常に1回の応答を返す。`waiting_editor_ready/queued` は `cancelled`、`running` は `cancel_requested`、終端済みは `rejected` とする。

## Execution Guarantee Rules
1. `waiting_editor_ready` で失敗した要求は `not_executed` とみなす。
2. `queued` で取消した要求は `not_executed` とみなす。
3. `running` で `ERR_RECONNECT_TIMEOUT` となった要求は結果不確定（`unknown`）とみなす。
4. `client_request_id` は将来の重複抑止向けidempotencyキーとして維持する（v1は受理のみ）。

## Consequences
1. 実装ごとの差異を減らし、再現性のある不具合調査が可能になる。
2. `cancel` 競合時の挙動が一定化される。
3. デメリットとして、状態遷移実装にCAS/排他制御が必要になる。

## Non-Goals (v1)
1. 優先度付き状態遷移
2. 分散ジョブスケジューラ
3. 並列実行数の動的拡張
