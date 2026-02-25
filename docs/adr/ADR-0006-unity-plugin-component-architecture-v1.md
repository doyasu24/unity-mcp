# ADR-0006: Unity Plugin内部コンポーネント構成 v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
ADR-0001〜0005でMCP Server側設計を確定した。  
本ADRでは、Unity Plugin側の内部構成とライフサイクル連携を定義する。

要件は以下。
1. `1 Editor = 1 MCP Server` 固定バインド
2. WebSocket通信のみ
3. compile/reload状態をMCPへ通知
4. 実行契約（sync/job, cancel）に応答可能
5. project-scope設定としてportをチーム共有可能

## Decision
Unity Pluginを以下のコンポーネントで構成する。

1. `PluginSettingsStore` (`ScriptableSingleton`)
2. `BridgeConnection`
3. `BridgeLifecycle`
4. `EditorStatePublisher`
5. `CommandRouter`
6. `CommandExecutor`
7. `JobExecutor`
8. `CancelRegistry`
9. `CapabilityPublisher`
10. `PluginLogger`
11. `PluginSettingsWindow` (`EditorWindow`)
12. PluginはWebSocketクライアントとしてMCP Server（listener）へ接続する。

## Component Responsibilities
### 1) PluginSettingsStore (`ScriptableSingleton`)
1. `ProjectSettings/UnityMcpPluginSettings.asset` を正本として設定を保持する。
2. `schemaVersion` / `port` を提供する。
3. `PluginSettingsWindow` からの保存要求で `Save(true)` を実行する。

### 2) BridgeConnection
1. MCP Server待受portへのWebSocketクライアント接続（connect/send/receive/close）を担当する。
2. `hello`, `editor_status`, `ping/pong`, `result` メッセージを送受信する。
3. 通信層は業務ロジックを持たない。

### 3) BridgeLifecycle
1. Editor起動、コンパイル、ドメインリロード、終了イベントを監視する。
2. 状態に応じて接続停止/再接続を制御する。
3. 再接続後に `hello` と `capability` を再送する。

### 4) EditorStatePublisher
1. `ready/compiling/reloading` を検知して `editor_status` を送信する。
2. `seq` は接続セッション単位で `1` から開始し単調増加で付与する（再接続時はリセット）。
3. 古い状態通知をServer側で破棄可能にする。
4. `seq` は符号なし64bit整数として扱う。

### 5) CommandRouter
1. 受信した `execute/cancel` を対応するhandlerへ振り分ける。
2. 未知commandは構造化エラーで返す。

### 6) CommandExecutor
1. `sync` commandをUnityメインスレッドで実行する。
2. 例外を `ERR_UNITY_EXECUTION` として返却する。

### 7) JobExecutor
1. `job` commandをキュー管理して順次実行する。
2. `queued/running/succeeded/failed/cancelled` を保持する。
3. v1は非永続（Editor再起動でjob状態は消失）。

### 8) CancelRegistry
1. 実行中command/jobに対応するcancel tokenを管理する。
2. `supports_cancel=false` は `cancel_requested` 応答を返す。

### 9) CapabilityPublisher
1. 起動時と再接続時に利用可能toolのメタデータを送る。
2. `execution_mode`, `supports_cancel`, timeout設定を含む。

### 10) PluginLogger
1. `request_id/client_request_id/job_id` をログ相関キーとして出力する。
2. compile/reload遷移、接続断、再接続、実行失敗を監査可能にする。

### 11) PluginSettingsWindow (`EditorWindow`)
1. `Unity MCP Settings` UIで `port` を編集可能にする。
2. `Apply` で値検証と接続切替を実行する。
3. 接続切替成功時のみ `PluginSettingsStore` へ永続化する。

## Lifecycle Integration
1. `Editor起動完了` -> WebSocket接続開始 -> `hello(state=ready)` 送信
2. `compile開始` -> `editor_status(compiling)` 送信
3. `before domain reload` -> 接続クローズ（可能なら通知）
4. `after domain reload` -> 再接続 -> `hello(state=reloading or ready)` 送信
5. `compile完了` -> `editor_status(ready)` 送信
6. `Editor終了` -> 接続クローズ

## Threading Model
1. Unity API呼び出しはメインスレッドのみ。
2. 通信I/Oは非同期タスクで処理する。
3. メインスレッドへは `CommandExecutor` のディスパッチ経由で切り替える。

## Message Handling Rules
1. `execute(sync)`  
   受信後、メインスレッドで実行し `result` を返す。
2. `submit_job`  
   `job_id` を発行し、`accepted` を返した後に実行する。
3. `cancel`  
   `supports_cancel=true` は中断処理、`false` は `cancel_requested` を返す。
4. `ping`  
   可能な限り `pong` を即時返す（compile/reload時は遅延/欠落あり）。可能なら `editor_state` と `seq` を同梱する。

## Error Mapping (Unity Side)
1. 未知command -> `ERR_UNKNOWN_COMMAND`
2. パラメータ不正 -> `ERR_INVALID_PARAMS`
3. 実行例外 -> `ERR_UNITY_EXECUTION`
4. cancel対象種別が契約外 -> `ERR_CANCEL_NOT_SUPPORTED`
5. cancel失敗 -> `ERR_CANCEL_REJECTED`

## Configuration Storage (Plugin v1)
```csharp
[FilePath("ProjectSettings/UnityMcpPluginSettings.asset", FilePathAttribute.Location.ProjectFolder)]
internal sealed class UnityMcpPluginSettings : ScriptableSingleton<UnityMcpPluginSettings>
{
    public int schemaVersion = 1;
    public int port = 48091;
}
```

## Consequences
1. Server側ADRと責務境界が対称になり、実装が揃う。
2. compile/reload時の状態通知が標準化され、待機復帰が安定する。
3. コンポーネント数が増えるため、初期実装は分割方針を厳守する必要がある。

## Non-Goals (v1)
1. マルチEditorを1 Pluginで束ねる設計
2. 永続jobストア
3. リモート認証/認可
