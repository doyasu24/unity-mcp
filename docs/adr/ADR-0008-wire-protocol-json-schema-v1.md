# ADR-0008: Wire Protocol JSON仕様 v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
ADR-0001〜0007で、構成・再接続・実行契約・Plugin再設定まで確定した。  
本ADRでは、Unity PluginとMCP Server間のWebSocketメッセージ仕様をv1として固定する。

目的は以下。
1. 実装間で相互運用可能な共通フォーマットを定義する
2. 失敗時の挙動を構造化エラーで統一する
3. 将来拡張時に互換性を保ちやすくする

## Decision
1. Wire ProtocolはUTF-8 JSONテキストフレームのみを採用する。
2. すべてのメッセージは `type` を必須とする。
3. request/response相関には `request_id` を使用する。
4. エラーは共通 `error` フォーマットで返す。
5. `protocol_version` は `1` 固定で開始する。
6. 接続方向は「Plugin(client) -> Server(listener)」で固定する。
7. heartbeat開始はServer側 `ping` とする。

## Common Envelope
すべてのメッセージは以下の共通フィールドを持つ。

```json
{
  "type": "string",
  "protocol_version": 1,
  "timestamp": "2026-02-24T23:59:59Z"
}
```

### Common Rules
1. `type`: 必須、snake_case
2. `protocol_version`: 必須、整数
3. `timestamp`: 任意、RFC3339 UTC推奨
4. 未知フィールドは受信側で無視してよい（forward compatibility）

## Connection Sequence (v1)
1. PluginがServerの待受 `port` へ接続する（Server/Pluginで同一値）。
2. Pluginが `hello(plugin_version, state)` を送信する。
3. Serverが `hello(server_version)` を返す。
4. Serverが `capability` を送信する。
5. 運用中のheartbeatは Server `ping` -> Plugin `pong` で維持する。

## Message Types (v1)
### 1) hello
接続直後の能力・状態初期同期に使用。

```json
{
  "type": "hello",
  "protocol_version": 1,
  "plugin_version": "1.0.0",
  "server_version": "1.0.0",
  "state": "ready"
}
```

Rules:
1. 送信側は自分側version（`plugin_version` または `server_version`）を必須で設定する。
2. 受信側versionフィールドは任意で、省略可能。
3. `state` はPlugin送信時に必須、Server送信時は任意。

### 2) capability
利用可能tool定義を送る。

```json
{
  "type": "capability",
  "protocol_version": 1,
  "tools": [
    {
      "name": "run_tests",
      "execution_mode": "job",
      "supports_cancel": true,
      "default_timeout_ms": 300000,
      "max_timeout_ms": 1800000,
      "requires_client_request_id": false,
      "execution_error_retryable": false
    }
  ]
}
```

Rules:
1. `execution_error_retryable` は予約フィールドとして保持し、v1では判定に使わなくてもよい。
2. 厳密な `retryable` マッピングは後続版で有効化する。

### 3) editor_status
Unity状態通知。`seq` は単調増加。

```json
{
  "type": "editor_status",
  "protocol_version": 1,
  "state": "ready",
  "seq": 42
}
```

`state` は `ready | compiling | reloading`。

Rules:
1. `seq` は接続セッション単位で `1` から開始する。
2. 再接続後は `seq` をリセットしてよい。
3. `seq` は符号なし64bit整数として扱う。v1ではラップアラウンドを考慮しない。

### 4) ping / pong
接続生存確認。

```json
{ "type": "ping", "protocol_version": 1 }
```
```json
{
  "type": "pong",
  "protocol_version": 1,
  "editor_state": "ready",
  "seq": 42
}
```

Rules:
1. heartbeatの開始はServer側 `ping` とする。
2. Pluginは可能な限り即時 `pong` を返す。
3. `pong.editor_state` と `pong.seq` は任意。存在する場合、Serverは状態再同期に使用してよい。

### 5) execute (sync)
短時間処理要求。

```json
{
  "type": "execute",
  "protocol_version": 1,
  "request_id": "req-100",
  "client_request_id": "uuid-optional",
  "tool_name": "read_console",
  "params": {},
  "timeout_ms": 30000
}
```

### 6) result (sync)
`execute` の応答。

```json
{
  "type": "result",
  "protocol_version": 1,
  "request_id": "req-100",
  "status": "ok",
  "result": {}
}
```

`status` は `ok | error`。
`result(status=error)` は「実行開始後に発生したtool実行エラー」に使用する。

### 7) submit_job
長時間処理開始要求。

```json
{
  "type": "submit_job",
  "protocol_version": 1,
  "request_id": "req-200",
  "client_request_id": "uuid-optional",
  "tool_name": "run_tests",
  "params": {},
  "timeout_ms": 300000
}
```

### 8) submit_job_result
`submit_job` の受付応答。

```json
{
  "type": "submit_job_result",
  "protocol_version": 1,
  "request_id": "req-200",
  "status": "accepted",
  "job_id": "job-1"
}
```

### 9) get_job_status
job状態照会。

```json
{
  "type": "get_job_status",
  "protocol_version": 1,
  "request_id": "req-201",
  "job_id": "job-1"
}
```

### 10) job_status
job状態応答。

```json
{
  "type": "job_status",
  "protocol_version": 1,
  "request_id": "req-201",
  "job_id": "job-1",
  "state": "running",
  "progress": null
}
```

`state` は `queued | running | succeeded | failed | timeout | cancelled`。

Rules:
1. v1では `job_progress` のpushメッセージを定義しない。
2. `progress` は任意で、未算出時は `null` を返してよい。

### 11) cancel
要求またはjobのキャンセル要求。

```json
{
  "type": "cancel",
  "protocol_version": 1,
  "request_id": "req-300",
  "target_request_id": "req-200",
  "target_job_id": "job-1"
}
```

`target_request_id` か `target_job_id` のいずれか必須。

### 12) cancel_result
`cancel` の結果応答。

```json
{
  "type": "cancel_result",
  "protocol_version": 1,
  "request_id": "req-300",
  "status": "cancelled"
}
```

`status` は `cancelled | cancel_requested | rejected`。

### 13) error
共通エラー応答。

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

`error` は主に「バリデーション/ルーティング/プロトコル」失敗に使用する。

## Error Code Set (v1)
1. `ERR_INVALID_REQUEST`
2. `ERR_INVALID_PARAMS`
3. `ERR_UNKNOWN_COMMAND`
4. `ERR_EDITOR_NOT_READY`
5. `ERR_UNITY_DISCONNECTED`
6. `ERR_RECONNECT_TIMEOUT`
7. `ERR_REQUEST_TIMEOUT`
8. `ERR_COMPILE_TIMEOUT`
9. `ERR_QUEUE_FULL`
10. `ERR_JOB_NOT_FOUND`
11. `ERR_CANCEL_NOT_SUPPORTED`
12. `ERR_CANCEL_REJECTED`
13. `ERR_UNITY_EXECUTION`
14. `ERR_INVALID_RESPONSE`
15. `ERR_RECONFIG_IN_PROGRESS`

`ERR_CANCEL_NOT_SUPPORTED` は「cancelを受け付けない対象種別」に対してのみ使用する。
`supports_cancel=false` の既知対象は `cancel_requested` を返す。

## Ordering and Idempotency Rules
1. `editor_status.seq` が小さい通知は破棄する。
2. 同一 `request_id` の重複 `result/error` は最初の1件のみ有効とする。
3. `client_request_id` は将来の重複抑止向けidempotencyキーとして予約する（v1は受理のみで重複防止には未使用）。
4. `get_job_status` は副作用なしで、同一要求の再試行を許容する。

## Size and Limits
1. 単一メッセージ最大サイズ: `1MB`
2. `params` が大きい場合は分割送信せず、別経路（ファイル参照等）を使う。
3. `tools` 配列は初期同期時に送る。頻繁な再送を避ける。
4. 受信側は上限超過メッセージを `ERR_INVALID_REQUEST` として扱い、必要に応じて接続を切断してよい。
5. 送信側は上限超過を送らない責務を持つ。受信側で上限超過を検知した場合、結果は `ERR_INVALID_RESPONSE` として扱ってよい。

## Compatibility Rules
1. `protocol_version` 不一致時は `error(code=ERR_INVALID_REQUEST)` を返し接続を終了してよい。
2. 未知 `type` は `error(code=ERR_UNKNOWN_COMMAND)` を返す。
3. 未知フィールドは無視する。

## Consequences
1. 実装チーム間で通信仕様が固定され、相互実装が容易になる。
2. 障害解析時にメッセージ単位で追跡しやすくなる。
3. デメリットとして、仕様厳格化により初期実装の自由度は下がる。

## Non-Goals (v1)
1. バイナリフレーム対応
2. 圧縮プロトコル
3. マルチプロトコル自動ネゴシエーション
