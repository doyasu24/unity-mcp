# ADR-0015: ハートビート廃止と接続簡素化 v1

- Status: Accepted
- Date: 2026-02-28
- Supersedes: ADR-0014 (heartbeat miss threshold), ADR-0002 (ping/pong reconnect)

## Context

Server-Unity Editor 間の接続は、カスタム ping/pong ハートビートプロトコル、6通りのセッション昇格結果（`Activated`, `AlreadyActive`, `ReplacedActiveSameEditor`, `ReplacedStaleSocket`, `RejectedActiveExists`, `UnknownSocket`）、連続ミス閾値追跡（`HeartbeatMissState`）など多くの可動部を持ち、エッジケースの原因となっていた。

CoplayDev/unity-mcp の参考実装では、専用ハートビートではなく定期的なデータフロー（ステータスファイル書き込み）を暗黙のハートビートとして使用しており、はるかにシンプルな設計で安定動作している。

## Decision

### 1. 専用 ping/pong プロトコルを廃止

Server は `ping` メッセージを送信しない。Plugin は `pong` メッセージを送信しない。

代わりに Plugin が `EditorStatusIntervalMs`（5秒）間隔で `editor_status` メッセージを定期送信する。Server はアクティブソケットからの任意のメッセージ受信タイムスタンプで接続生存を判定する（暗黙のハートビート）。

### 2. Stale connection timer

Server は `StaleConnectionTimeoutMs`（15秒）以内にアクティブソケットからメッセージを受信しなかった場合、接続を切断する。チェック間隔は `StaleConnectionTimeoutMs / 3`（5秒）。

### 3. セッション管理を3通りに簡素化

`SessionPromotionResult`（6通り）を `AcceptResult`（3通り）に置換:

| AcceptResult | 意味 |
|---|---|
| `Accepted` | アクティブ接続なし、同一ソケットの再受理、または閉じたソケットの置換 |
| `Replaced` | 同一エディタの再接続（旧ソケットを返却しクローズ） |
| `Rejected` | 別エディタがアクティブ中 |

`_registeredSockets` HashSet を削除（1:1 モデルで不要）。`Register()` メソッドを削除（`TryAccept` のみで完結）。`RecordExchange()` / staleness detection を全削除。

### 4. WebSocket keepalive 短縮

`ServerHost.cs` の `KeepAliveInterval` を `30s → 10s` に変更。localhost 前提で応答性を重視。TCP レベルの keepalive がトランスポート生存性を担保する。

### 5. 定数変更

| 旧 | 新 |
|---|---|
| `HeartbeatIntervalMs = 3000` | 削除 |
| `HeartbeatTimeoutMs = 12000` | 削除 |
| `HeartbeatMissThreshold = 2` | 削除 |
| `SessionStaleThresholdMs = 15000` | 削除 |
| — | `StaleConnectionTimeoutMs = 15000` |
| — | `EditorStatusIntervalMs = 5000` |

### 6. RuntimeState 簡素化

- `OnPong()` メソッドを削除（エディタ状態は `OnEditorStatus()` のみから取得）
- `_lastPongUtc` → `_lastMessageReceivedUtc` にリネーム
- `RecordMessageReceived()` メソッドを追加

### 7. hello レスポンス変更

Server hello レスポンスから `heartbeat_interval_ms`, `heartbeat_timeout_ms`, `heartbeat_miss_threshold` を除去。`editor_status_interval_ms` を追加（情報提供のみ）。

## Consequences

### Positive

- プロトコルメッセージ2種廃止（ping/pong）
- Server ~150行削減
- Plugin のハートビート応答コードをゼロに
- セッション管理の分岐を6→3に半減
- `HeartbeatMissState` クラスとテストを完全削除

### Negative

- 旧 Plugin（pong 送信）が新 Server に接続すると `Unhandled message` 警告が出る（機能影響なし）
- 旧 Server（ping 送信 + pong 必須）に新 Plugin が接続すると heartbeat miss で切断される
- v0.1.0 未リリース前提のため、バージョンスキューは考慮しない

### compile/reload 時の安定性

compile 中に WebSocket が切断されても、Server は直前の `editor_status` で `compiling` 状態を記憶しており、`CompileGraceTimeoutMs=90000` まで待機する。この挙動は変更前と同一。

### マルチエディタ拒否は維持

異なるエディタからの接続は引き続き `Rejected` で拒否する。ただし staleness による強制置換は削除（1:1 モデルの原則に厳格に従う）。
