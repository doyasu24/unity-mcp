# ADR-0012: v1 Toolカタログとメタデータ固定

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
ADR-0004で分類基準を定義した。  
本ADRでは、初期実装（v1.0）で実際に公開するtoolを最小構成で固定する。

目的は以下。
1. 実装対象を最小化して早期に動作を成立させる
2. `run_tests` を `job` として安全に運用する
3. 未実装toolの誤公開を防ぐ

## Decision
1. v1.0初期実装の公開toolは `read_console`, `get_editor_state`, `run_tests`, `get_job_status`, `cancel_job` の5つに限定する。
2. `run_tests` は `execution_mode=job`, `supports_cancel=true` で固定する。
3. `get_job_status` と `cancel_job` は `run_tests(job)` を運用するための制御toolとして必須とする。
4. `capability.tools` には実装済みtoolのみを公開する。
5. 追加toolは後続ADRまたは本ADR改訂で段階追加する。

## Metadata Schema (v1)
```json
{
  "name": "string",
  "execution_mode": "sync|job",
  "supports_cancel": true,
  "default_timeout_ms": 30000,
  "max_timeout_ms": 30000,
  "requires_client_request_id": false,
  "execution_error_retryable": false
}
```
注記: `execution_error_retryable` は予約フィールド。v1.0では厳密判定に使わなくてもよい。

## Initial Tool Catalog (v1.0)
| name | kind | execution_mode | supports_cancel | default_timeout_ms | max_timeout_ms | requires_client_request_id | execution_error_retryable |
|---|---|---|---|---:|---:|---|---|
| `get_editor_state` | read | `sync` | false | 5000 | 10000 | false | true |
| `read_console` | read | `sync` | false | 10000 | 30000 | false | true |
| `run_tests` | read_heavy | `job` | true | 300000 | 1800000 | false | false |
| `get_job_status` | control | `sync` | false | 5000 | 10000 | false | false |
| `cancel_job` | control | `sync` | false | 5000 | 10000 | false | false |

## Control Tool Rules
1. `get_job_status` は `job_id` を必須引数とする。
2. `cancel_job` は `job_id` を必須引数とする。
3. 未知 `job_id` は `ERR_JOB_NOT_FOUND` を返す。
4. `run_tests` の `job_id` が未発行（例: `waiting_editor` timeout）の場合、制御tool呼び出し対象は存在しない。

## Global Rules
1. `timeout_ms` 未指定時は `default_timeout_ms` を適用する。
2. `timeout_ms` が `max_timeout_ms` を超える場合は `ERR_INVALID_PARAMS`。
3. `supports_cancel=false` のtoolは `cancel_requested` を返して処理継続する（ADR-0003）。
4. `requires_client_request_id=true` のtoolで未指定なら `ERR_INVALID_REQUEST`。
5. `run_tests` はv1では読み取り系契約として扱うため `requires_client_request_id=false` とする。
6. Play Modeテスト等で重複実行を強く避けたい運用では、クライアント側で `client_request_id` を任意付与してよい。
7. `run_tests` はログ出力など副作用を持ち得るが、v1では「プロジェクト状態を永続変更しないことを前提とした read_heavy 契約」として扱う。
8. `ERR_UNITY_EXECUTION` の `retryable` 厳密判定（`execution_error_retryable` 参照）は後続版で有効化する。

## Deferred Tools (Post v1.0)
以下はv1.0では非公開とし、後続で追加検討する。
1. `ping`
2. `list_scenes`
3. `list_assets`
4. `get_selection`
5. `open_scene`
6. `build_player`
7. `import_assets_bulk`
8. `generate_code_project_wide`
9. `bake_lighting`
10. `bake_navmesh`
11. `script_recompile_trigger`

## Capability Publication Rules
1. `capability.tools` には本ADRの初期5toolのみを公開する。
2. 未実装toolは公開しない（公開済みで未実装は契約違反）。
3. 同一toolのメタデータ変更はADR改訂を必須とする。

## Change Control
1. tool追加/削除はADR改訂を必須とする。
2. `execution_mode` 変更はADR改訂を必須とする。
3. `supports_cancel` を `false -> true` に変更する場合、実装検証結果をPRに添付する。

## Consequences
1. 初期実装スコープが明確になり、着手しやすい。
2. `run_tests(job)` の最小運用に必要な制御面を欠かさない。
3. デメリットとして、初期段階では利用可能な機能が限定される。

## Non-Goals (v1.0)
1. 実行時の自動mode切替
2. toolごとの動的SLA学習
3. capabilityの差分配信
