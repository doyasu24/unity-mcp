# ADR-0010: 起動ライフサイクルとリクエスト受理ポリシー v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
ADR-0002〜0004で再接続、実行モデル、tool分類を定義したが、
MCP Server起動直後（Unity未接続）の受理ポリシーと状態遷移が未固定だった。

`1 Editor = 1 MCP Server` 構成では、Server単体は先に起動し、Unity接続を後追いで待つ運用が現実的である。
そのため、起動成功条件・待機中の実行可否・未実行保証を明示する。

## Decision
1. MCP ServerはUnity未接続でも起動成功とする。
2. 起動直後は `waiting_editor` 状態に入り、Unity接続後に `ready` へ遷移する。
3. `waiting_editor` 中の `sync/job` 要求は短時間待機し、復帰した場合のみ実行する。
4. `waiting_editor` の待機上限は `request_reconnect_wait_ms` を使用する。
5. 待機上限超過時は `ERR_EDITOR_NOT_READY` を返し、未実行を保証する。
6. `submit_job` が待機上限超過した場合は `job_id` を発行しない。
7. `waiting_editor` 待機中の `cancel` は即時 `cancelled` とする。
8. `waiting_editor -> ready` 復帰時の再開順序はFIFOとする。

注記: `request_reconnect_wait_ms` など運用定数の正本は `docs/spec/IMPLEMENTATION_SPEC_V1.md` Section 10 とする。

## State Model (Server Runtime)
1. `booting`
2. `waiting_editor`
3. `ready`
4. `stopping`
5. `stopped`

補足:
1. `compiling/reloading` はEditor状態であり、Server接続状態とは別軸で扱う（ADR-0002）。
2. `waiting_editor` は「未接続」を意味し、`compiling/reloading` とは区別する。

## Startup Sequence
1. Server起動
2. 設定読み込み・検証（ADR-0009）
3. MCP endpoint公開
4. Unity Plugin向けWebSocket待受開始
5. 接続成功まで `waiting_editor`
6. `hello` 交換成功後に `ready`

## Request Acceptance Rules
### A. `ready`
1. `sync` は通常実行。
2. `submit_job` は `job_id` を発行して通常実行。

### B. `waiting_editor`
1. `sync` は最大 `request_reconnect_wait_ms` まで待機。
2. `submit_job` も同じく待機。
3. 復帰成功時のみ実行へ進む。
4. 復帰失敗時は `ERR_EDITOR_NOT_READY`。

### C. Timeout on `waiting_editor`
1. Unityへ未送信を保証する。
2. `sync` は失敗応答のみ返す。
3. `submit_job` は `job_id` 未発行で失敗応答を返す。

## Cancel Rules on `waiting_editor`
1. 待機キュー上の要求は即時 `cancelled`。
2. cancel完了時点で当該要求は再開対象から除外する。
3. `cancelled` 後にUnity復帰しても実行しない。

## Resume Ordering
1. `waiting_editor` 中に積まれた要求はFIFOで再開する。
2. `sync` と `job` を混在させても到着順を維持する。
3. v1では優先度キューを導入しない。

## Shutdown Rules
1. `stopping` へ遷移した時点で新規要求受理を停止する。
2. `queued/waiting_editor_ready` 要求は `ERR_EDITOR_NOT_READY` で失敗終了する。
3. `running` 要求は `ERR_RECONNECT_TIMEOUT` として終了する（`execution_guarantee` を返す実装では `unknown`）。
4. Pluginへの専用shutdownメッセージはv1では定義せず、接続クローズで通知する。

## Error Codes (Additions)
1. `ERR_EDITOR_NOT_READY`

## Observability
1. 状態遷移ログ: `booting -> waiting_editor -> ready`
2. 待機開始ログ: `request_id`, `tool_name`, `entered_waiting_editor_at`
3. 待機失敗ログ: `request_id`, `elapsed_ms`, `error_code=ERR_EDITOR_NOT_READY`
4. 待機キャンセルログ: `request_id`, `status=cancelled`

## Consequences
1. Server先行起動が可能になり、運用しやすい。
2. 待機上限と未実行保証により、再試行戦略が明確になる。
3. `submit_job` の不完全受付（`job_id` だけ発行される状態）を回避できる。
4. デメリットとして、待機キュー制御の実装が必要になる。

## Non-Goals (v1)
1. 無期限待機キュー
2. 優先度付き再開順序
3. `waiting_editor` 専用の別タイムアウト設定追加
