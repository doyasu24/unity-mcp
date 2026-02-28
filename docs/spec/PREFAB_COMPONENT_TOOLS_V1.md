# Prefab Component Tools 仕様 v1

- Status: Draft
- Date: 2026-02-28
- Target: `Server` / `UnityMCPPlugin`
- 前提: SCENE_AND_COMPONENT_TOOLS_V1.md（以下「Scene 仕様」）を参照。共通概念（Field Value Format、シリアライズルール、展開制限等）は重複を避けて参照する。

## 1. 目的

MCP から Prefab アセットの構造を把握し、Prefab 内の GameObject にコンポーネントをアタッチし、SerializeField の値（Prefab 内のオブジェクト参照を含む）を設定できるようにする。

Scene ツールが「開かれたシーン上のオブジェクト」を対象とするのに対し、Prefab ツールは「AssetDatabase 上の `.prefab` ファイル」を対象とする。

LLM のワークフロー:
1. `get_prefab_hierarchy` で Prefab 内の階層構造を把握する（GO 名、パス、コンポーネント型名一覧）
2. `get_prefab_component_info` で対象コンポーネントのフィールド詳細を確認する（index で指定）
3. `manage_prefab_component` でコンポーネントの追加・更新・削除・並べ替えを行う

## 2. ツール名の変更と追加

### 2.1 既存ツールのリネーム

Scene 専用であることを明確にするため、以下のリネームを行う:

| 旧名 | 新名 | 理由 |
|---|---|---|
| `get_component_info` | `get_scene_component_info` | Scene 操作であることを明示 |
| `manage_component` | `manage_scene_component` | Scene 操作であることを明示 |
| `get_scene_hierarchy` | （変更なし） | 既に `scene` を含む |

リネームに伴う変更対象:
- Server: `ToolNames`, `ToolCatalog`, `ToolContracts`, `Mcp.cs`, `UnityBridge.cs`
- Plugin: `ToolNames`, `CommandExecutor.cs`
- MCP tool description

### 2.2 新規追加ツール

| # | ツール名 | 種別 | execution_mode | 役割 |
|---|---|---|---|---|
| 1 | `get_prefab_hierarchy` | read-only | sync | Prefab 内の木構造を返す（GO 名、パス、コンポーネント型名一覧） |
| 2 | `get_prefab_component_info` | read-only | sync | Prefab 内の特定 GO のコンポーネント（index 指定）のフィールド詳細を返す |
| 3 | `manage_prefab_component` | edit | sync | Prefab 内のコンポーネントの追加・更新・削除・並べ替え |

### 2.3 対称的な命名

| Scene | Prefab |
|---|---|
| `get_scene_hierarchy` | `get_prefab_hierarchy` |
| `get_scene_component_info` | `get_prefab_component_info` |
| `manage_scene_component` | `manage_prefab_component` |

---

## 3. Scene ツールとの差分

### 3.1 アクセスパターン

| 操作 | Scene ツール | Prefab ツール |
|---|---|---|
| 読み取り | `SceneManager.GetActiveScene()` → `GameObject.Find()` | `AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path)` |
| 書き込み | `Undo.AddComponent` → `EditorSceneManager.SaveOpenScenes()` | `PrefabUtility.LoadPrefabContents(path)` → 編集 → `PrefabUtility.SaveAsPrefabAsset()` → `PrefabUtility.UnloadPrefabContents()` |

### 3.2 パス体系

| 概念 | Scene ツール | Prefab ツール |
|---|---|---|
| アセット識別 | なし（アクティブ Scene 暗黙） | `prefab_path`（例: `"Assets/Prefabs/Player.prefab"`） |
| オブジェクトパス | Scene hierarchy 絶対パス（例: `"/Canvas/Panel"`） | Prefab ルートからの相対パス（例: `"/Child"`, `""` = ルート） |
| ルート名 | パスに含む（`"/Player"`） | パスに含まない（`""` or `"/"` or 省略 = ルート） |

### 3.3 参照解決

| 参照種類 | Scene ツール | Prefab ツール |
|---|---|---|
| `$ref` | Scene hierarchy から解決 | Prefab 内 hierarchy から解決 |
| `$asset` | `AssetDatabase.LoadAssetAtPath` | 同一 |
| 出力の `ref_path` | Scene hierarchy パス | Prefab 内相対パス |

### 3.4 Undo / 原子性

| 概念 | Scene ツール | Prefab ツール |
|---|---|---|
| Undo 統合 | あり | なし（`LoadPrefabContents` は Undo 非対応） |
| 原子性 | Undo グループ + validate-then-apply | validate-then-apply + save-or-discard |

### 3.5 Play Mode ガード

Scene ツールは `manage_scene_component` で Play Mode 中エラー。Prefab ツールは Play Mode に依存しないためガード不要。

### 3.6 ネスト Prefab

`get_prefab_hierarchy` の各ノードに `nested_prefab_asset_path` を付与することで、MCP client がネスト Prefab インスタンスかどうかを判断できるようにする。

---

## 4. `get_prefab_hierarchy` (read-only)

### 4.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "prefab_path": {
      "type": "string",
      "description": "Asset path of the Prefab (e.g. \"Assets/Prefabs/Player.prefab\")."
    },
    "game_object_path": {
      "type": "string",
      "description": "Optional: path within the Prefab to start from. \"\" or \"/\" or omitted = root."
    },
    "max_depth": {
      "type": "integer",
      "minimum": 0,
      "maximum": 50,
      "default": 10,
      "description": "Maximum depth of the hierarchy tree to traverse. 0 returns only the specified level."
    },
    "max_game_objects": {
      "type": "integer",
      "minimum": 1,
      "maximum": 10000,
      "default": 1000,
      "description": "Maximum number of GameObjects to include in the response."
    }
  },
  "required": ["prefab_path"],
  "additionalProperties": false
}
```

### 4.2 出力仕様

```json
{
  "prefab_path": "Assets/Prefabs/Player.prefab",
  "prefab_name": "Player",
  "root": {
    "name": "Player",
    "path": "",
    "active": true,
    "components": ["UnityEngine.Transform", "UnityEngine.Rigidbody", "MyGame.PlayerController"],
    "children": [
      {
        "name": "Model",
        "path": "/Model",
        "active": true,
        "components": ["UnityEngine.Transform", "UnityEngine.MeshRenderer", "UnityEngine.MeshFilter"],
        "children": []
      },
      {
        "name": "WeaponSlot",
        "path": "/WeaponSlot",
        "active": true,
        "nested_prefab_asset_path": "Assets/Prefabs/Weapon.prefab",
        "components": ["UnityEngine.Transform"],
        "children": [
          {
            "name": "Blade",
            "path": "/WeaponSlot/Blade",
            "active": true,
            "components": ["UnityEngine.Transform", "UnityEngine.MeshRenderer"],
            "children": []
          }
        ]
      }
    ]
  },
  "total_game_objects": 4,
  "truncated": false
}
```

フィールド定義:
1. `prefab_path`: 対象 Prefab のアセットパス（入力と同値）
2. `prefab_name`: Prefab ルート GameObject の名前
3. `root`: ルート GameObject ノード。Scene ツールの `root_game_objects`（配列）と異なり、Prefab はルートが 1 つなので単一オブジェクト
4. 各ノードの `path`: Prefab ルートからの相対パス。ルート自身は `""`。子は `"/Child"`, 孫は `"/Child/GrandChild"`。ルート名は含まない
5. 各ノードの `nested_prefab_asset_path`（optional）: ネスト Prefab インスタンスのルートの場合のみ付与。Prefab 自身のルートには付与しない
6. その他（`name`, `active`, `components`, `children`, `total_game_objects`, `truncated`）: Scene 仕様 3.2 節と同一ルール

### 4.3 動作ルール
1. `prefab_path` の拡張子が `.prefab` でない場合は `ERR_INVALID_PARAMS`。
2. `AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path)` でロード。null なら `ERR_PREFAB_NOT_FOUND`。
3. `game_object_path` 省略/空文字列/`"/"` → Prefab ルートを起点。指定時は 4.4 節で検索。見つからなければ `ERR_OBJECT_NOT_FOUND`。
4. ネスト Prefab 判定: `PrefabUtility.IsAnyPrefabInstanceRoot(go) && go != prefabRoot`。
5. 走査ルール（幅優先、max_depth/max_game_objects）は Scene 仕様 3.3 節と同一。
6. 読み取りは `AssetDatabase.LoadAssetAtPath` を使用（`LoadPrefabContents` は不要）。
7. Unity メインスレッドで実行。

### 4.4 Prefab 内パスの解決ロジック

新設 `PrefabGameObjectResolver`:

1. `Resolve(GameObject prefabRoot, string path)`:
   - null / 空文字列 / `"/"` → `prefabRoot` を返す
   - `TrimStart('/')` で正規化 → `prefabRoot.transform.Find(normalized)` で検索
   - 見つからない場合は null

2. `GetRelativePath(GameObject prefabRoot, GameObject target)`:
   - `target == prefabRoot` → `""` を返す
   - Transform 階層をたどりルート名を除いたパスを構築（例: `"/Model"`, `"/WeaponSlot/Blade"`）

### 4.5 メタデータ

| 項目 | 値 |
|---|---|
| `execution_mode` | `sync` |
| `supports_cancel` | `false` |
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `execution_error_retryable` | `true` |

### 4.6 エラー仕様

| エラーコード | 発生条件 | retryable |
|---|---|---|
| `ERR_PREFAB_NOT_FOUND` | Prefab アセットが見つからない | false |
| `ERR_OBJECT_NOT_FOUND` | `game_object_path` が Prefab 内に見つからない | false |
| `ERR_INVALID_PARAMS` | パラメータ不正、拡張子が `.prefab` でない | false |

---

## 5. `get_prefab_component_info` (read-only)

### 5.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "prefab_path": {
      "type": "string",
      "description": "Asset path of the Prefab."
    },
    "game_object_path": {
      "type": "string",
      "description": "Path within the Prefab. \"\" or \"/\" = root."
    },
    "index": {
      "type": "integer",
      "minimum": 0,
      "description": "0-based component index from get_prefab_hierarchy."
    },
    "fields": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Optional: field names to return."
    },
    "max_array_elements": {
      "type": "integer",
      "minimum": 0,
      "maximum": 64,
      "default": 16
    }
  },
  "required": ["prefab_path", "game_object_path", "index"],
  "additionalProperties": false
}
```

### 5.2 出力仕様

```json
{
  "prefab_path": "Assets/Prefabs/Player.prefab",
  "game_object_path": "",
  "game_object_name": "Player",
  "index": 2,
  "component_type": "MyGame.PlayerController",
  "fields": {
    "speed": 5.0,
    "weapon": {
      "type": "UnityEngine.Transform",
      "value": "WeaponSlot (Transform)",
      "is_object_ref": true,
      "ref_path": "/WeaponSlot"
    },
    "alertMaterial": {
      "type": "UnityEngine.Material",
      "value": "Alert (Material)",
      "is_asset_ref": true,
      "asset_path": "Assets/Materials/Alert.mat"
    }
  }
}
```

フィールド値のシリアライズルールは Scene 仕様 4.3 節と同一。

### 5.3 参照の出力（Scene ツールとの差分）

Prefab 内オブジェクトは `EditorUtility.IsPersistent(obj)` が `true` を返すため、Scene ツールの判別ロジックでは不十分。追加判定:
- `AssetDatabase.GetAssetPath(obj) == prefab_path` → 同一 Prefab 内参照 → `is_object_ref: true` + `ref_path`（Prefab 内パス）
- それ以外の Asset → `is_asset_ref: true` + `asset_path`

### 5.4 動作ルール
1. `.prefab` 拡張子チェック → `ERR_INVALID_PARAMS`。
2. `AssetDatabase.LoadAssetAtPath<GameObject>` でロード → null なら `ERR_PREFAB_NOT_FOUND`。
3. `game_object_path` で検索（4.4 節）→ 見つからなければ `ERR_OBJECT_NOT_FOUND`。
4. `index` でコンポーネント取得 → 範囲外は `ERR_COMPONENT_INDEX_OUT_OF_RANGE`、null は `ERR_MISSING_SCRIPT`。
5. `FieldSerializer` でシリアライズ。`ref_path` は `PrefabGameObjectResolver.GetRelativePath` で算出。参照判別は 5.3 節のロジック。
6. Unity メインスレッドで実行。

### 5.5 メタデータ

`get_prefab_hierarchy` と同一。

### 5.6 エラー仕様

| エラーコード | 発生条件 | retryable |
|---|---|---|
| `ERR_PREFAB_NOT_FOUND` | Prefab が見つからない | false |
| `ERR_OBJECT_NOT_FOUND` | GO が見つからない | false |
| `ERR_COMPONENT_INDEX_OUT_OF_RANGE` | index 範囲外 | false |
| `ERR_MISSING_SCRIPT` | Missing Script | false |
| `ERR_INVALID_PARAMS` | パラメータ不正 | false |

---

## 6. `manage_prefab_component` (edit)

### 6.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "prefab_path": {
      "type": "string",
      "description": "Asset path of the Prefab."
    },
    "action": {
      "type": "string",
      "enum": ["add", "update", "remove", "move"],
      "description": "Operation to perform."
    },
    "game_object_path": {
      "type": "string",
      "description": "Path within the Prefab. \"\" or \"/\" = root."
    },
    "component_type": { "type": "string", "description": "Required for 'add'." },
    "index": { "type": "integer", "minimum": 0, "description": "Required for 'update'/'remove'/'move'." },
    "new_index": { "type": "integer", "minimum": 0, "description": "Required for 'move'." },
    "fields": {
      "type": "object",
      "description": "Field values. Format は Scene 仕様 5.2 節と同一。$ref は Prefab 内から解決。",
      "additionalProperties": true
    }
  },
  "required": ["prefab_path", "action", "game_object_path"],
  "additionalProperties": false
}
```

action ごとの必須パラメータは Scene 仕様 5.1 節と同一。

### 6.2 出力仕様

Scene 仕様 5.3 節と同一構造 + `prefab_path` フィールド。例:

```json
{
  "action": "add",
  "prefab_path": "Assets/Prefabs/Player.prefab",
  "game_object_path": "",
  "game_object_name": "Player",
  "component_type": "MyGame.PlayerController",
  "index": 3,
  "fields_set": ["speed", "weapon"],
  "fields_skipped": []
}
```

### 6.3 動作ルール

#### 6.3.1 共通
1. **Play Mode ガード: なし**。
2. `.prefab` 拡張子チェック → `ERR_INVALID_PARAMS`。
3. `PrefabUtility.LoadPrefabContents(prefab_path)` でロード → null なら `ERR_PREFAB_NOT_FOUND`。
4. `game_object_path` で検索（4.4 節）→ 見つからなければ `ERR_OBJECT_NOT_FOUND`（`UnloadPrefabContents` で破棄）。

#### 6.3.2 事前検証による原子性（save-or-discard）

**Phase 1: 検証（状態変更なし）**
1. GO 解決、型解決、コンポーネント取得
2. `fields` 内の `$ref` → `PrefabGameObjectResolver.Resolve(root, path)` で検索
3. `fields` 内の `$asset` → `AssetDatabase.LoadAssetAtPath` で読み込み
4. 1 件でも失敗 → `ERR_REFERENCE_NOT_FOUND` で失敗、`UnloadPrefabContents` で破棄

**Phase 2: 適用**
5. `go.AddComponent(type)`（add の場合、Undo なし）
6. `SerializedObject` / `SerializedProperty` で値設定
7. `ApplyModifiedPropertiesWithoutUndo()`
8. `PrefabUtility.SaveAsPrefabAsset(root, prefab_path)` で保存
9. `PrefabUtility.UnloadPrefabContents(root)` でクリーンアップ

**例外安全:** 全体を `try-finally` で囲み、finally で必ず `UnloadPrefabContents` を呼ぶ。

#### 6.3.3 `add`
Scene 仕様 5.4.2 節と同一。`Undo.AddComponent` → `go.AddComponent(type)` に置換。

#### 6.3.4 `update`
Scene 仕様 5.4.3 節と同一。Undo 省略。

#### 6.3.5 `remove`
Scene 仕様 5.4.4 節と同一。`Undo.DestroyObjectImmediate` → `Object.DestroyImmediate` に置換。`[RequireComponent]` チェックは再利用。

#### 6.3.6 `move`
Scene 仕様 5.4.5 節と同一。`ComponentUtility.MoveComponentUp/Down` で移動。Undo 省略。

#### 6.3.7 フィールド設定（`add`/`update` 共通）
Scene 仕様 5.4.6 節と同一（`FieldDeserializer.Apply` を再利用）。差分:
- `ApplyModifiedProperties()` → `ApplyModifiedPropertiesWithoutUndo()`
- `RecordPrefabInstancePropertyModifications` は呼ばない

#### 6.3.8 `$ref` 解決
`GameObjectResolver.Resolve(path)` → `PrefabGameObjectResolver.Resolve(prefabRoot, path)` に差し替え。コンポーネント取得ロジックは同一。

### 6.4 メタデータ

| 項目 | 値 |
|---|---|
| `execution_mode` | `sync` |
| `supports_cancel` | `false` |
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `execution_error_retryable` | `false` |

### 6.5 エラー仕様

| エラーコード | 発生条件 | retryable |
|---|---|---|
| `ERR_PREFAB_NOT_FOUND` | Prefab が見つからない | false |
| `ERR_OBJECT_NOT_FOUND` | GO が見つからない | false |
| `ERR_COMPONENT_TYPE_NOT_FOUND` | 型が見つからない（`add`） | false |
| `ERR_COMPONENT_TYPE_AMBIGUOUS` | 型が複数一致（`add`） | false |
| `ERR_INVALID_COMPONENT_TYPE` | `Component` 非継承（`add`） | false |
| `ERR_COMPONENT_INDEX_OUT_OF_RANGE` | index 範囲外 | false |
| `ERR_MISSING_SCRIPT` | Missing Script | false |
| `ERR_REFERENCE_NOT_FOUND` | `$ref`/`$asset` 参照先なし | false |
| `ERR_COMPONENT_DEPENDENCY` | `[RequireComponent]` 依存（`remove`） | false |
| `ERR_PREFAB_SAVE_FAILED` | `SaveAsPrefabAsset` 失敗 | false |
| `ERR_INVALID_PARAMS` | パラメータ不正 | false |

---

## 7. 使用例

### 対象 Prefab の初期状態

`Assets/Prefabs/Player.prefab`:
```
Player (root): [Transform(0), Rigidbody(1), CapsuleCollider(2)]
  Model: [Transform(0), MeshRenderer(1), MeshFilter(2)]
  WeaponSlot: [Transform(0)]
```

### 7.1 Prefab 構造の把握

```json
{ "name": "get_prefab_hierarchy", "arguments": { "prefab_path": "Assets/Prefabs/Player.prefab" } }
```
→ root ノードに Player の全階層が返る。

### 7.2 コンポーネント詳細確認

```json
{ "name": "get_prefab_component_info", "arguments": {
    "prefab_path": "Assets/Prefabs/Player.prefab", "game_object_path": "", "index": 1
} }
```
→ Rigidbody のフィールド値が返る。

### 7.3 スクリプトの追加と SerializeField 設定

```json
{ "name": "manage_prefab_component", "arguments": {
    "prefab_path": "Assets/Prefabs/Player.prefab",
    "action": "add", "game_object_path": "",
    "component_type": "PlayerController",
    "fields": {
      "speed": 5.0,
      "weapon": { "$ref": "/WeaponSlot", "component": "Transform" },
      "alertMaterial": { "$asset": "Assets/Materials/Alert.mat" }
    }
} }
```
→ PlayerController が index=3 に追加。`$ref` は Prefab 内の WeaponSlot を参照。

### 7.4 子 GO のコンポーネント操作

```json
{ "name": "manage_prefab_component", "arguments": {
    "prefab_path": "Assets/Prefabs/Player.prefab",
    "action": "add", "game_object_path": "/WeaponSlot",
    "component_type": "BoxCollider"
} }
```

---

## 8. メタデータ一覧

| ツール名 | execution_mode | supports_cancel | default_timeout_ms | max_timeout_ms | execution_error_retryable |
|---|---|---|---:|---:|---|
| `get_prefab_hierarchy` | sync | false | 10000 | 30000 | true |
| `get_prefab_component_info` | sync | false | 10000 | 30000 | true |
| `manage_prefab_component` | sync | false | 10000 | 30000 | false |

---

## 9. 設計意図

### 9.1 別ツールとする理由
今後の機能追加・変更に備え、Scene ツールと Prefab ツールを分離する。将来的に Prefab 固有の操作（GO 追加/削除、Variant 作成等）を追加する際に、Scene ツールへの影響を避けられる。

### 9.2 既存ツールのリネーム
`get_component_info` / `manage_component` を `get_scene_component_info` / `manage_scene_component` にリネームし、Scene/Prefab の対称性を明確にする。

### 9.3 Prefab 内パス設計
ルート名を含まない相対パス。Prefab 名の変更に影響されない。

### 9.4 Undo 非対応
`LoadPrefabContents` / `SaveAsPrefabAsset` は Undo 非対応。save-or-discard で原子性を保証。

### 9.5 コード再利用
`FieldSerializer` / `FieldDeserializer` / `ComponentTypeResolver` は再利用。差分は:
1. `ref_path` 算出: `GameObjectResolver.GetHierarchyPath` → `PrefabGameObjectResolver.GetRelativePath`
2. `$ref` 解決: `GameObjectResolver.Resolve` → `PrefabGameObjectResolver.Resolve`
3. 参照分類: `IsPersistent` + `GetAssetPath == prefab_path` の追加判定

---

## 10. エッジケース

1. **存在しない Prefab**: `ERR_PREFAB_NOT_FOUND`
2. **`.prefab` 以外の拡張子**: `ERR_INVALID_PARAMS`
3. **ネスト Prefab への変更**: 外側 Prefab に override が記録。ネスト元は変更されない
4. **Prefab Variant**: 同一ワークフローで操作可能。変更は Variant の override として記録
5. **同時実行**: `RequestScheduler` がグローバルに直列化するため競合なし
6. **例外安全**: `try-finally` で `UnloadPrefabContents` を保証

---

## 11. 将来の拡張候補（v1 では非対応）
1. Prefab 内の GameObject の追加・削除・リネーム
2. Prefab の新規作成
3. Prefab Variant の作成
4. 複数コンポーネントの一括操作
