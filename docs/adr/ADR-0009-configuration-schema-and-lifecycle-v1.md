# ADR-0009: 設定ファイル仕様とライフサイクル v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
ADR-0001〜0008でアーキテクチャ、実行契約、Wire Protocolを確定した。  
本ADRでは、MCP Server/Unity Pluginの設定方式をゼロコンフィグ前提で統一する。

目的は以下。
1. 初期セットアップなしで動くこと（zero-config）
2. 必要時だけ設定ファイルを書き換えて変更できること
3. 設定項目を最小化し、運用ミスを減らすこと

## Decision
1. デフォルトでは設定ファイルなしで起動できる（missing fileは正常系）。
2. 設定変更はJSON設定ファイルの書き換えで行う。
3. v1で外部設定として公開する項目は `port` のみとする。
4. `host` はローカル固定 (`127.0.0.1`) とし、設定項目にしない。
5. 再接続・heartbeat・queue等は内部固定値（ADR-0002/0005既定値）として扱う。
6. 設定ファイルが不正な場合はエラーとして起動を停止する（hard fail）。
7. 設定ファイルの保存は原子的に行う（tempファイル -> rename）。
8. `unity_ws_port` は「MCP ServerのWebSocket待受port」、`server_port` は「Pluginが接続するServer port」を意味する。

## File Layout (v1)
1. MCP Server (optional): `./unity-mcp.server.json`
2. Unity Plugin (optional): `UserSettings/UnityMcpPluginSettings.json`

どちらも存在しなければ既定値で動作する。

## Server Config Schema (v1)
```json
{
  "schema_version": 1,
  "unity_ws_port": 8091
}
```

## Plugin Config Schema (v1)
```json
{
  "schema_version": 1,
  "server_port": 8091
}
```

## Validation Rules
### Common
1. 未知フィールドは警告ログを出して無視する。
2. `schema_version` は省略可。存在する場合は `1` のみ許可する。
3. 数値項目は整数/範囲検証を必須とする。

### Server
1. `unity_ws_port` は `1..65535`。

### Plugin
1. `server_port` は `1..65535`。

## Apply Timing Rules
### Server
1. 起動時に設定ファイルを読み込む。
2. 設定変更はServer再起動時に反映する。

### Plugin
1. Editor起動時に設定ファイルを読み込む。
2. Runtimeのport変更API成功時は、再接続後に設定ファイルへ保存する。
3. 手動で設定ファイルを書き換えた場合は、次回Editor起動時に反映する。

## Persistence Rules
### Common
1. 既定値運用時は設定ファイルを作成しない。
2. 非既定値に変更されたときのみ設定ファイルを作成/更新する。
3. 既定値へ戻した場合は、設定ファイルを削除してもよい。

### Plugin
1. Runtime変更が成功した場合のみ保存する。
2. 変更適用失敗時はファイルを書き換えない（ロールバック）。

## Precedence Rules
### Server
1. CLI引数 (`--unity-ws-port`)
2. `unity-mcp.server.json`
3. 既定値 (`8091`)

### Plugin
1. Runtime APIでの変更結果
2. `UserSettings/UnityMcpPluginSettings.json`
3. 既定値 (`8091`)

## Operational Constraints (v1)
1. マルチEditor運用では、各 `MCP Server/Plugin` ペアに異なるportを手動設定する必要がある。
2. 既定値 `8091` は単一Editor運用を前提とし、複数インスタンス同時起動時はport競合する。
3. `host=127.0.0.1` 固定のため、ServerとEditorが同一ネットワーク名前空間に存在する前提とする。
4. Docker/WSL2等で名前空間が分離される場合、v1標準構成では接続対象外とする。

## Error Handling
1. 設定JSON parse失敗時は `ERR_CONFIG_PARSE` を記録し、起動を停止する。
2. 検証失敗時は `ERR_CONFIG_VALIDATION` を記録し、起動を停止する。
3. `schema_version` が不正な場合は `ERR_CONFIG_SCHEMA_VERSION` を記録し、起動を停止する。
4. 保存失敗時はエラーを返し、現行接続状態は維持する。

## Migration Policy
1. `schema_version=1` のみ正式サポートする。
2. 将来版（`schema_version>1`）を検出した場合は互換外として起動を停止する。

## Team Sharing Policy (Plugin)
1. Plugin設定は `UserSettings` 配下を使用し、個人環境設定として扱う。
2. チーム共有はv1の標準運用対象外とする。

## Consequences
1. 単一Editor運用では実質ゼロ設定で開始できる。
2. 必要時のみ設定ファイル編集でport変更できる。
3. 外部設定が最小化され、運用ミス面積を縮小できる。
4. デメリットとして、再接続詳細パラメータの外部チューニングはv1で不可となる。

## Non-Goals (v1)
1. リモート設定配布
2. 暗号化設定ストア
3. 複数環境プロファイル切替
4. host可変設定
5. 自動port割当・portレンジ探索
6. Docker/WSL2向けネットワーク透過サポート
