# ADR-0009: 設定ファイル仕様とライフサイクル v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
ADR-0001〜0008でアーキテクチャ、実行契約、Wire Protocolを確定した。  
本ADRでは、MCP Server/Unity Pluginの設定方式を定義する。

目的は以下。
1. 初期セットアップなしで動くこと（zero-config）
2. 必要時のみ設定を変更できること
3. チーム共有設定と個人設定を明確に分離すること
4. 設定項目を最小化し、運用ミスを減らすこと

## Decision
1. v1で外部設定として公開する項目は `port` のみとする。
2. `host` はローカル固定 (`127.0.0.1`) とし、設定項目にしない。
3. Server設定はCLI引数 `--port` のみで受け付ける（設定ファイルなし）。
4. Plugin設定は `ScriptableSingleton` を用いて `ProjectSettings/UnityMcpPluginSettings.asset` に保存する。
5. Pluginのport変更は `EditorWindow`（`Unity MCP Settings`）から行う。
6. Plugin設定は project-scope 設定として扱い、コミット対象とする。
7. 再接続・heartbeat・queue等は内部固定値（ADR-0002/0005既定値）として扱う。
8. 設定値が不正な場合はエラーとして初期化を停止する（hard fail）。
9. `port` は「MCP Serverの待受port」かつ「Pluginの接続先port」を同一値で表す。
10. 互換運用は行わない。`UserSettings` 形式の旧設定は読み込まず、移行もしない。

## File Layout (v1)
1. MCP Server: なし（CLI `--port` のみ）
2. Unity Plugin (optional): `ProjectSettings/UnityMcpPluginSettings.asset`

Plugin設定アセットが存在しない場合は既定値で動作する。

## Server Config Input (v1)
1. CLI引数: `--port`
2. 既定値: `48091`

## Plugin Settings Schema (ScriptableSingleton v1)
```csharp
[FilePath("ProjectSettings/UnityMcpPluginSettings.asset", FilePathAttribute.Location.ProjectFolder)]
internal sealed class UnityMcpPluginSettings : ScriptableSingleton<UnityMcpPluginSettings>
{
    public int schemaVersion = 1;
    public int port = 48091;
}
```

## Validation Rules
### Common
1. `schemaVersion` は `1` のみ許可する。
2. 数値項目は整数/範囲検証を必須とする。

### Server
1. `--port` は `1..65535`。

### Plugin
1. `port` は `1..65535`。

## Apply Timing Rules
### Server
1. 起動時にCLI引数を読み込む。
2. 設定変更はServer再起動時に反映する。

### Plugin
1. Editor起動時に `ScriptableSingleton` から設定を読み込む。
2. `Unity MCP Settings` EditorWindowで `Apply` した値を検証し、接続切替を実行する。
3. 接続切替成功時のみ `Save(true)` で `ProjectSettings` へ永続化する。
4. 失敗時は旧値を維持し、永続化しない。

## Persistence Rules
1. Plugin設定は `ScriptableSingleton` 経由で `ProjectSettings/UnityMcpPluginSettings.asset` を更新する。
2. 永続化はEditorWindowの `Apply` 成功時のみ行う。
3. 設定アセットはproject-scopeとしてVCSへコミットする。

## Precedence Rules
### Server
1. CLI引数 (`--port`)
2. 既定値 (`48091`)

### Plugin
1. EditorWindowで適用済みの `ScriptableSingleton` メモリ値
2. `ProjectSettings/UnityMcpPluginSettings.asset`
3. 既定値 (`48091`)

## Operational Constraints (v1)
1. マルチEditor運用では、各 `MCP Server/Plugin` ペアに異なるportを手動設定する必要がある。
2. 既定値 `48091` は単一Editor運用を前提とし、複数インスタンス同時起動時はport競合する。
3. `host=127.0.0.1` 固定のため、ServerとEditorが同一ネットワーク名前空間に存在する前提とする。
4. Docker/WSL2等で名前空間が分離される場合、v1標準構成では接続対象外とする。

## Error Handling
1. 設定読み込み失敗時は `ERR_CONFIG_PARSE` を記録し、初期化を停止する。
2. 検証失敗時は `ERR_CONFIG_VALIDATION` を記録し、初期化を停止する。
3. `schemaVersion` が不正な場合は `ERR_CONFIG_SCHEMA_VERSION` を記録し、初期化を停止する。
4. 永続化失敗時はエラーを返し、現行接続状態は維持する。

## Migration Policy
1. `schemaVersion=1` のみ正式サポートする。
2. `UserSettings` 旧設定からの自動移行は行わない。
3. 後方互換読み込みは提供しない。

## Team Sharing Policy (Plugin)
1. Plugin設定は `ProjectSettings` 配下を使用し、project-scope設定として扱う。
2. `port` の変更は設定アセット差分としてチームへ共有する（コミット対象）。

## Consequences
1. 設定責務が「ServerはCLI」「PluginはProjectSettings」に明確化される。
2. Unity側port設定をチームで統一できる。
3. EditorWindow経由での変更に統一され、運用手順が単純化される。
4. 互換性を持たないため、旧設定資産は再作成が必要になる。

## Non-Goals (v1)
1. リモート設定配布
2. 暗号化設定ストア
3. 複数環境プロファイル切替
4. host可変設定
5. 自動port割当・portレンジ探索
6. Docker/WSL2向けネットワーク透過サポート
