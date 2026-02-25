# ADR-0002: WebSocket接続・再接続・Compile状態連携設計 v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
ADR-0001で `1 Editor = 1 MCP Server` を採用した。  
本ADRは、Unity接続プロトコルと再接続挙動をv1として確定する。

ローカル用途では復帰速度を優先したい。一方で、Unityのコンパイル/ドメインリロード中は一時的に無応答になり得るため、通常時と同じタイムアウト判定では誤失敗が増える。

## Decision
1. Unity接続プロトコルはWebSocketのみを採用する。
2. Unityは `editor_status` イベントで状態を通知する。
3. MCP Serverは状態に応じてタイムアウト/保留挙動を切り替える。
4. 再接続は指数バックオフ（短め）を採用する。
5. compile/reload中はheartbeat欠落を即異常判定しない。
6. 接続方向は「PluginがWebSocketクライアント、MCP ServerがWebSocketサーバー（listener）」で固定する。
7. Serverは同時に1つのPluginセッションのみ受け付ける。
8. heartbeatの送信開始はServer側（`ping`）とする。

## Protocol Contract (v1)
1. `hello`  
   接続確立直後に双方がversion情報と現在状態を交換する。
2. `editor_status`  
   Unity -> MCP。状態遷移通知に使用する。
3. `ping` / `pong`  
   接続生存確認に使用する。
4. `execute` / `result`  
   tool実行要求と応答に使用する。

## Connection Topology (v1)
1. MCP Serverは `port` で待受する（CLI `--port`）。
2. Unity Pluginは同一 `port` に接続する。
3. 切断時はPluginが指数バックオフで再接続を試行し、Serverは待受を継続する。

### Message Shapes (v1)
```json
{ "type":"hello", "protocol_version":1, "plugin_version":"1.2.0", "server_version":"1.2.0", "state":"ready" }
```
```json
{ "type":"editor_status", "protocol_version":1, "state":"compiling", "seq":42, "timestamp":"2026-02-24T23:59:59Z" }
```
```json
{ "type":"execute", "protocol_version":1, "request_id":"req-123", "tool_name":"read_console", "params":{}, "timeout_ms":30000 }
```
```json
{ "type":"result", "protocol_version":1, "request_id":"req-123", "status":"ok", "result":{} }
```

## Editor State Model
1. `ready`
2. `compiling`
3. `reloading`

MCP Serverは最後に受信した `editor_status` をメモリ保持する。  
再接続後の `hello.state` を正として状態を再同期する。

## Reconnect Strategy (Local-First)
以下はPlugin側再接続の既定値（Serverは待受継続）とする。

1. `reconnect_initial_ms = 100`
2. `reconnect_multiplier = 1.7`
3. `reconnect_max_backoff_ms = 1200`
4. `reconnect_jitter_ratio = 0.1`

## Timeout Policy
1. `heartbeat_interval_ms = 3000`
2. `heartbeat_timeout_ms = 4500` (通常時)
3. `request_reconnect_wait_ms = 2500` (通常切断時の待機上限)
4. `compile_grace_timeout_ms = 60000` (compile/reload時の待機上限)

注記: 既定値の正本は `docs/spec/IMPLEMENTATION_SPEC_V1.md` の定数テーブル（Section 10）とする。

## Heartbeat Policy
1. Serverが `heartbeat_interval_ms` ごとに `ping` を送る。
2. Pluginは可能な限り即時 `pong` を返す。
3. `heartbeat_timeout_ms` 内に `pong` がない場合、Serverは接続断として扱い `waiting_editor` へ遷移する。
4. `pong` には任意で `editor_state` と `seq` を含めてよい。Serverはこれで状態を補正してよい。

## State Freshness Rules
1. `recently observed compile/reload` は次を満たす場合に成立する。  
   - `last_editor_state in {compiling, reloading}`  
   - `now - last_editor_state_at <= compile_grace_timeout_ms`  
   - それ以降に `ready` 状態を受信していない
2. `last_editor_state_at` は `editor_status` 受信時刻（または `pong` 補正時刻）を使う。
3. `editor_status` が喪失しても、`pong(editor_state, seq)` でServer状態を再同期してよい。

## Request Handling Rules
1. `state = ready`  
   即時実行する。
2. `state = compiling | reloading`  
   リクエストは保留し、`ready` 復帰で実行する。
3. `disconnected` かつ compile/reload状態を直近で観測済み  
   `compile_grace_timeout_ms` まで保留する。
4. `disconnected` かつ compile/reload状態が不明  
   `request_reconnect_wait_ms` まで待機し、復帰不可なら失敗する。

## Failure Codes (v1)
1. `ERR_UNITY_DISCONNECTED`
2. `ERR_RECONNECT_TIMEOUT`
3. `ERR_REQUEST_TIMEOUT`
4. `ERR_COMPILE_TIMEOUT`
5. `ERR_UNITY_EXECUTION`
6. `ERR_INVALID_RESPONSE`
7. `ERR_EDITOR_NOT_READY` (起動直後/再接続中の待機上限超過)

## Consequences
1. ローカル環境での復帰速度を優先できる。
2. compile/reload中の誤タイムアウトを減らせる。
3. 状態通知が欠落しても、再接続時 `hello.state` で再同期できる。
4. デメリットとして、状態機械が増え実装は同期単発より複雑になる。

## Non-Goals (v1)
1. 複数transportの同時サポート
2. リモート公開向け認証設計
3. メトリクス/トレース導入
