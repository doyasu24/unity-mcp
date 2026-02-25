# ADR-0001: Unity MCP Server Architecture v1

- Status: Proposed
- Version: 1
- Date: 2026-02-24

## Context
Unity用MCPサーバーを新規実装する。  
目的は、複数のUnity Editorを同時に扱える構成を、シンプルな運用で実現すること。

単一MCPサーバーで複数Editorを切り替える方式は、同時実行時の責務境界と障害分離が曖昧になりやすい。  
そのため、v1では「分離を優先した構成」を採用する。

## Decision
1. `1 Editor = 1 MCP Server` を完全固定する。
2. 各MCP Serverは専用portで起動し、対応するUnity Editorも同じportに設定する。
3. Claude/Codex側では、複数MCP Serverを個別エントリとして登録する。
4. ルーティングは「接続時固定バインド」とし、tool呼び出し時の追加パラメータは持たない。
5. 切断中のtool呼び出しは「短時間待機し、復帰できた場合のみ実行（待機復帰型）」とする。
6. 接続検証のための追加ハッシュキーは導入しない。
7. セキュリティ境界はローカル限定とする。
8. Plugin/Serverのバージョン不一致は警告ログのみ（接続拒否しない）。
9. 観測性はログ中心とし、メトリクス/トレースはv1スコープ外とする。

## Architecture Outline
1. Process topology  
   `Unity Editor A <-> MCP Server A`  
   `Unity Editor B <-> MCP Server B`  
   `Unity Editor C <-> MCP Server C`
2. Binding model  
   各MCP Serverは単一Editor専用接続のみ保持する。
3. Isolation model  
   EditorまたはMCP Serverの障害は、同一ペアに閉じる。

## Runtime State (In-Memory in MCP Server)
1. `connection_state` (`connected`, `disconnected`, `reconnecting`)
2. `reconnect_state` (`retry_count`, `next_retry_at`, `max_wait`)
3. `inflight_requests` (`request_id`, `started_at`, `deadline`, `status`)
4. `pending_during_reconnect` (短期TTLバッファ)
5. `cancel_tokens` (キャンセル可能tool用)
6. `capability_snapshot` (接続中Unityの利用可能機能)
7. `version_state` (plugin/serverバージョン情報)

## Consequences
1. メリット: 同時実行が自然に成立する。
2. メリット: 障害分離が明確で、デバッグ時に原因切り分けしやすい。
3. デメリット: MCP Serverプロセス数とクライアント設定数が増える。
4. デメリット: 誤port設定などの運用ミスは起動時/接続時エラーとして検出する運用が必要。

## Non-Goals (v1)
1. 単一HubでのマルチEditor集約運用
2. 認証/認可付きのリモート公開
3. メトリクス/トレース基盤の導入
4. バージョン互換モードの実装

## Follow-up ADRs
1. ADR-0002: WebSocket接続・再接続・Compile状態連携
2. ADR-0003: Tool実行モデル・キュー・キャンセル契約
3. ADR-0004: Tool分類と実行モード割当
