# ADR-0007: Unity Pluginのport変更・再接続設計 v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
`1 Editor = 1 MCP Server` 構成では、Unity Plugin側で接続先portを変更できることが運用上必須となる。  
本ADRは、port変更を安全に適用するための契約を定義する。

ゼロコンフィグ方針のため、hostは固定 (`127.0.0.1`) とし、v1で可変にするのはportのみとする。
PluginはWebSocketクライアントとして、指定された `server_port` へ接続する。

## Decision
1. Unity Pluginは接続先 `server_port` のランタイム変更をサポートする。
2. 適用モードはv1で `immediate` を採用する（変更後すぐ切替）。
3. 設定永続化は「新portへの接続成功後」に行う。
4. 切替失敗時は旧portへ自動ロールバックする。
5. 再設定操作は同時実行不可とし、排他制御する。

## API Contract (Plugin Internal)
1. `set_server_port(port, apply_mode="immediate") -> ReconfigureResult`
2. `get_server_port() -> int`
3. `test_server_port(port, timeout_ms) -> TestResult`

`ReconfigureResult`:
1. `status`: `applied | rolled_back | failed`
2. `active_port`: 現在有効なport
3. `error_code` / `error_message` (失敗時)

## Validation Rules
1. `port` は `1..65535` の整数であること。
2. 現在値と同一port指定は no-op (`status=applied`)。
3. 変更中に再変更要求が来た場合は `ERR_RECONFIG_IN_PROGRESS` を返す。

## Apply Procedure (Immediate Mode)
1. `BridgeLifecycle` が `reconfig_lock` を取得する。
2. `BridgeConnection` の送受信ループを停止する（理由: `port_reconfigure`）。
3. 新portを一時適用し、接続試行を開始する。
4. 接続成功後、以下を順に送信する。
   - `hello`
   - `capability`
   - 現在の `editor_status`
5. すべて成功したら新portを永続化し、`status=applied` で完了する。

## Rollback Procedure
1. 新port接続に失敗した場合、旧portへ再接続を試行する。
2. 旧port復帰に成功した場合は `status=rolled_back` とする。
3. 旧port復帰にも失敗した場合は `status=failed` とし、未接続状態を明示する。
4. 失敗時はUI/ログへ「現在未接続」であることを明示する。

## Timeout and Retry (Port Reconfigure)
1. `reconfigure_connect_timeout_ms = 5000`
2. `reconfigure_max_attempts = 3`
3. `reconfigure_retry_interval_ms = 200`

この値はv1では内部固定値（設定ファイル外）とする。

## Interaction with Compile/Reload
1. `compiling/reloading` 中のport変更要求は受理し、再接続可能時点で適用する。
2. 状態遷移中に接続が落ちても、適用フローの最終成功条件は `hello + capability + editor_status` の再送完了とする。

## In-Flight Request Rules During Reconfigure
1. reconfigure開始時の接続切替は、Server側からは通常の切断イベントとして扱う。
2. `running` 要求は `request_reconnect_wait_ms` 内に同一Editorへ再接続できれば継続し、超過時は `ERR_RECONNECT_TIMEOUT` で失敗する。
3. `running` 要求が `ERR_RECONNECT_TIMEOUT` で終了した場合、`execution_guarantee` を返す実装では `unknown` を設定する。
4. `queued/waiting_editor_ready` 要求は接続復帰後にFIFOで再開する。
5. reconfigure操作自体はPlugin内部操作であり、個別 `request_id` を要求しない。

## State Machine
1. `connected`
2. `reconfiguring`
3. `reconfig_applied`
4. `reconfig_rolled_back`
5. `disconnected`

遷移中は `reconfig_lock` により単一フローのみ許可する。

## Error Codes
1. `ERR_INVALID_PORT`
2. `ERR_RECONFIG_IN_PROGRESS`
3. `ERR_RECONFIG_CONNECT_TIMEOUT`
4. `ERR_RECONFIG_ROLLBACK_FAILED`

## Logging/Audit Fields
1. `old_port`
2. `new_port`
3. `result_status`
4. `error_code`
5. `elapsed_ms`
6. `request_id` (存在する場合)

## Persisted Setting (Plugin v1)
```json
{
  "schema_version": 1,
  "server_port": 8091
}
```

## Consequences
1. port変更時の挙動が deterministic になり、運用事故を減らせる。
2. 失敗時に自動ロールバックできるため、接続喪失時間を短くできる。
3. 追加の状態遷移と排他制御が必要になり、Plugin実装はやや複雑化する。

## Non-Goals (v1)
1. 複数endpointの同時接続
2. host可変設定
3. 自動port探索・自動port割当
