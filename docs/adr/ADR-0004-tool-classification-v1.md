# ADR-0004: Tool分類と実行モード割当 v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
ADR-0003で `sync/job` の実行モード契約を定義した。  
本ADRは、各toolをどのモードに割り当てるかを決める判定ルールと初期分類を定義する。

v1では「実装者判断で毎回決める」ことを避け、同じ条件なら同じモードになる基準を固定する。

## Decision
1. 各toolは `execution_mode` を必須メタデータとして宣言する。
2. 割当は「5秒以内完了見込み」「キャンセル必要性」「副作用強度」で決める。
3. v1の既定は `sync`。ただし判定基準に合致するものは `job` を必須とする。
4. `job` toolは `supports_cancel` の明示を必須とする。
5. `client_request_id` は変更系toolのみ必須とする。
6. `job` は非永続とし、MCP Server再起動時に消失する。
7. 初期実装で実際に公開するtoolスコープは ADR-0012 を正とする。

## Classification Criteria
### A. Duration Expectation
1. 通常運用で5秒以内完了が見込まれる -> `sync`
2. 5秒超の可能性がある -> `job`

補足（5秒基準）:
1. 5秒は「対話的操作として待機可能な上限」の運用ルールであり、分類判定にのみ使う。
2. `sync_default_timeout_ms=30000` は異常時の吸収余地（瞬間的負荷/一時遅延）であり、通常期待時間を30秒に引き上げる意味ではない。
3. 5秒基準を恒常的に超えるtoolは、実測統計ではなく運用ルールに従って `job` へ昇格する。

### B. Cancelability Need
1. ユーザーが中断したくなる処理（長時間、重負荷、外部副作用あり）は `job`
2. 中断価値が低い短時間処理は `sync`

### C. Side Effect Strength
1. 読み取り主体（状態取得、一覧取得） -> `sync`
2. プロジェクト全体に影響する処理（テスト全実行、ビルド、大量import） -> `job`

### D. Compile/Reload Dependency
1. 実行後にcompile/reload待機を伴う処理 -> `job`
2. compile/reloadの影響を受けない軽量参照系 -> `sync`

### E. Tie-Break Rule
1. 判定に迷う場合は `job` を採用する（安全側）。

## Timeout Defaults by Mode
1. `sync_fast_timeout_ms = 5000`
2. `sync_default_timeout_ms = 30000`
3. `job_default_timeout_ms = 300000`
4. `job_max_timeout_ms = 1800000`

## Required Metadata
```json
{
  "name": "run_tests",
  "execution_mode": "job",
  "supports_cancel": true,
  "default_timeout_ms": 300000,
  "max_timeout_ms": 1800000
}
```

## Reference Assignment Table (Classification Baseline)
### sync
1. `ping`
2. `get_editor_state`
3. `read_console`
4. `list_scenes`
5. `list_assets`
6. `get_selection`
7. `open_scene` (単一シーン切替で短時間な場合)

### job
1. `run_tests`
2. `build_player`
3. `import_assets_bulk`
4. `generate_code_project_wide`
5. `bake_lighting`
6. `bake_navmesh`
7. `script_recompile_trigger`（実行後にcompile/reload待機が必要なもの）

## Promotion/Demotion Rules
1. `sync` toolでタイムアウト/再試行が継続発生し、5秒以内完了見込みを満たせないと判断した場合、次版で `job` に昇格する。
2. `job` toolが短時間化しても、強副作用またはcompile/reload依存がある場合は `job` を維持する。
3. `execution_mode` の変更はADR更新を必須とする。

## Interaction with ADR-0002/0003
1. `compiling/reloading` 状態時は、`sync` も `job` も `waiting_editor_ready` に入る。
2. `job` は `job_id` で再取得可能とし、切断復帰後の二重実行を防ぐ。
3. `client_request_id` は変更系toolで必須、読み取り系toolでは任意とする。
4. v1では単一キュー運用を採用し、制御toolを含めて到着順で処理する。
5. バイパス最適化は後続版で追加検討する。

## Cancel Behavior (v1)
1. `supports_cancel=true` のtoolは通常のcancel契約を適用する。
2. `supports_cancel=false` のtoolに `cancel` が来た場合は `cancel_requested` を即時返す。
3. `cancel_requested` の場合、実処理は継続し、終了時に通常結果または失敗を返す。

## Consequences
1. 実装時のモード選択が一貫する。
2. 長時間処理を早期に `job` 化でき、運用時のタイムアウト事故を減らせる。
3. デメリットとして、tool追加時にメタデータ設計が必須になる。

## Non-Goals (v1)
1. 動的な自動モード切替
2. 優先度付きマルチキュー
3. SLA自動学習によるモード推定
