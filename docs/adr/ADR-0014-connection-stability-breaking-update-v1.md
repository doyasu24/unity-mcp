# ADR-0014: 接続安定性最優先の破壊的更新（protocol_version=1据え置き）

- Status: Accepted
- Version: 1
- Date: 2026-02-27

## Context
既存実装は heartbeat/再接続待機が短く、compile/reload中の一時切断で誤失敗しやすかった。  
また、切断時の失敗意味論（実行前失敗/実行中断）と返却契約が曖昧で、MCP Client側の再試行判断が難しかった。

## Decision
1. `protocol_version` は `1` のまま維持し、互換性は維持しない（未リリース前提の破壊的変更）。
2. Plugin `hello` は `editor_instance_id`, `plugin_session_id`, `connect_attempt_seq` を必須化する。
3. Server `hello` は `connection_id`, `heartbeat_interval_ms`, `heartbeat_timeout_ms`, `heartbeat_miss_threshold` を返す。
4. heartbeatは連続ミス閾値方式へ変更する（単発遅延で切断しない）。
5. 待機ポリシーを待機理由別に分岐する。
   - reconnecting: `request_reconnect_wait_ms = 45000`
   - compiling/reloading: `compile_grace_timeout_ms = 90000`
6. エラー返却は `retryable`, `details.execution_guarantee`, `details.recovery_action` を必須化する。
7. Serverは同一 `request_id` の自動再送を行わない。

## Failure Semantics (Server視点)
1. 実行前失敗: `execute` 未送信で失敗。  
   - `retryable=true`
   - `execution_guarantee=not_executed`
   - `recovery_action=retry_allowed`
2. 実行中断: `execute` 送信済み・結果受信前に切断/超時。  
   - `retryable=false`
   - `execution_guarantee=unknown`
   - `recovery_action=inspect_state_then_retry_if_needed`

## Operational Defaults
1. `reconnect_initial_ms = 200`
2. `reconnect_multiplier = 1.8`
3. `reconnect_max_backoff_ms = 5000`
4. `reconnect_jitter_ratio = 0.2`
5. `heartbeat_interval_ms = 3000`
6. `heartbeat_timeout_ms = 12000`
7. `heartbeat_miss_threshold = 2`
8. `request_reconnect_wait_ms = 45000`
9. `compile_grace_timeout_ms = 90000`
10. `queue_max_size = 128`

## Consequences
1. 誤切断と誤失敗を抑制できる。
2. 実行中断時の再試行責務をMCP Clientへ明確に委譲できる。
3. `protocol_version=1` のまま破壊的変更を含むため、旧Plugin/旧Serverの混在運用は想定しない。

## Supersedes
本ADRは、接続安定性と失敗意味論に関する既存ADR/Specの矛盾する記述に優先する。
