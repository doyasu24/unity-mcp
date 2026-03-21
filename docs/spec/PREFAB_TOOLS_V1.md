# Prefab Tools 仕様 v1

- Status: Draft
- Date: 2026-03-02
- Target: `Server` / `UnityMCPPlugin`
- 前提: SCENE_TOOLS_V1.md（以下「Scene 仕様」）を参照。共通概念（Field Value Format、FieldSerializer/FieldDeserializer、ComponentTypeResolver、offset ベースページネーション）は Scene 仕様 §3 で定義されている。
- 注: MCP レイヤーでは Scene/Prefab ツールが統合済み（`get_hierarchy`, `get_component_info`, `manage_component`, `find_game_objects`, `manage_game_object`）。`prefab_path` パラメータの有無で Scene/Prefab モードが切り替わる。本仕様は Prefab モード固有の動作を記述する。

## 1. 目的

MCP から Prefab アセットの構造を把握し、Prefab 内の GameObject にコンポーネントをアタッチし、SerializeField の値を設定し、GameObject の作成・更新・削除・検索を行えるようにする。

Scene ツールが「開かれたシーン上のオブジェクト」を対象とするのに対し、Prefab ツールは「AssetDatabase 上の `.prefab` ファイル」を対象とする。

LLM のワークフロー（統合ツール名、`prefab_path` 指定 = Prefab モード）:
1. `get_hierarchy` で Prefab 内の階層構造を把握する
2. `get_component_info` で対象コンポーネントのフィールド詳細を確認する
3. `manage_component` でコンポーネントの追加・更新・削除・並べ替えを行う
4. `find_game_objects` で条件に合う GameObject を検索する
5. `manage_game_object` で GO の作成・更新・削除・親子関係変更を行う

## 2. ツール一覧

| # | MCP ツール名 | wire protocol 名 | 種別 | timeout(ms) |
|---|---|---|---|---|
| 1 | `get_hierarchy` (prefab_path 指定) | `get_prefab_hierarchy` | read-only | 10000/30000 |
| 2 | `get_component_info` (prefab_path 指定) | `get_prefab_component_info` | read-only | 10000/30000 |
| 3 | `manage_component` (prefab_path 指定) | `manage_prefab_component` | edit | 10000/30000 |
| 4 | `find_game_objects` (prefab_path 指定) | `find_prefab_game_objects` | read-only | 10000/30000 |
| 5 | `manage_game_object` (prefab_path 指定) | `manage_prefab_game_object` | edit | 10000/30000 |

timeout 列は `default_timeout_ms / max_timeout_ms`。

---

## 3. Scene ツールとの差分

### 3.1 パス体系

| 概念 | Scene ツール | Prefab ツール |
|---|---|---|
| アセット識別 | なし（アクティブ Scene 暗黙） | `prefab_path`（例: `"Assets/Prefabs/Player.prefab"`） |
| オブジェクトパス | Scene hierarchy 絶対パス（例: `"/Canvas/Panel"`） | Prefab ルートからの相対パス（例: `"/Child"`, `""` = ルート） |
| ルート名 | パスに含む（`"/Player"`） | パスに含まない（`""` or `"/"` or 省略 = ルート） |

### 3.2 参照解決

| 参照種類 | Scene ツール | Prefab ツール |
|---|---|---|
| `$ref` | `GameObjectResolver.Resolve(path)` で Scene hierarchy から解決 | `PrefabGameObjectResolver.Resolve(prefabRoot, path)` で Prefab 内から解決 |
| `$asset` | `AssetDatabase.LoadAssetAtPath` | 同一 |
| 出力の `ref_path` | Scene hierarchy パス | Prefab 内相対パス |

Prefab 内オブジェクトは `EditorUtility.IsPersistent(obj)` が `true` を返すため、Scene ツールの判別ロジックでは不十分。追加判定:
- `AssetDatabase.GetAssetPath(obj) == prefab_path` → 同一 Prefab 内参照 → `is_object_ref: true` + `ref_path`
- それ以外の Asset → `is_asset_ref: true` + `asset_path`

### 3.3 書き込みパターン

| 操作 | Scene ツール | Prefab ツール |
|---|---|---|
| 読み取り | `SceneManager.GetActiveScene()` → `GameObject.Find()` | `AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path)` |
| 書き込み | `Undo.AddComponent` → `EditorSceneManager.SaveOpenScenes()` | `PrefabUtility.LoadPrefabContents(path)` → 編集 → `PrefabUtility.SaveAsPrefabAsset()` → `PrefabUtility.UnloadPrefabContents()` |
| Undo | あり | なし（`LoadPrefabContents` は Undo 非対応） |
| 原子性 | Undo グループ + validate-then-apply | validate-then-apply + save-or-discard |

### 3.4 Play Mode ガード

Scene ツールは変更系で Play Mode 中エラー。Prefab ツールは Play Mode に依存しないためガード不要。

### 3.5 active 判定

| ツール | active 判定 |
|---|---|
| `find_game_objects` Scene モード | `go.activeInHierarchy` |
| `find_game_objects` Prefab モード | `go.activeSelf` |

---

## 4. PrefabGameObjectResolver

Prefab 内のパス解決を担う。

1. `Resolve(GameObject prefabRoot, string path)`:
   - null / 空文字列 / `"/"` → `prefabRoot` を返す
   - `TrimStart('/')` で正規化 → `prefabRoot.transform.Find(normalized)` で検索
   - 見つからない場合は null

2. `GetRelativePath(GameObject prefabRoot, GameObject target)`:
   - `target == prefabRoot` → `""` を返す
   - Transform 階層をたどりルート名を除いたパスを構築（例: `"/Model"`, `"/WeaponSlot/Blade"`）

---

## 5. `get_hierarchy` — Prefab モード (read-only)

### 5.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "prefab_path": {
      "type": "string",
      "description": "Asset path of the Prefab (e.g. \"Assets/Prefabs/Player.prefab\")."
    },
    "root_path": {
      "type": "string",
      "description": "Hierarchy path of a root GameObject to start from. Omit for entire scene/Prefab."
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
      "description": "Maximum number of GameObjects to include. Response is truncated when exceeded. Use 'root_path' to drill into a subtree."
    },
    "offset": {
      "type": "integer",
      "minimum": 0,
      "default": 0,
      "description": "Number of results to skip. Use 'next_offset' from a truncated response to fetch the next page."
    },
    "component_filter": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Filter to GameObjects with at least one of these component types (matches Type.Name or FullName, case-insensitive). Tree mode (offset=0): non-matching GOs keep structure but omit components. Flat mode (offset>0): non-matching GOs excluded."
    }
  },
  "required": ["prefab_path"],
  "additionalProperties": false
}
```

### 5.2 出力仕様

#### offset=0（デフォルト）: ネストツリー形式

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

#### offset > 0: フラット配列形式

```json
{
  "prefab_path": "Assets/Prefabs/Player.prefab",
  "prefab_name": "Player",
  "game_objects": [
    { "name": "Model", "path": "/Model", "active": true, "components": [...] },
    { "name": "WeaponSlot", "path": "/WeaponSlot", "active": true, "components": [...] }
  ],
  "total_game_objects": 2,
  "truncated": true,
  "next_offset": 5
}
```

フィールド定義:
1. `prefab_path`: 対象 Prefab のアセットパス（入力と同値）
2. `prefab_name`: Prefab ルート GameObject の名前
3. `root`: ルート GameObject ノード（`offset=0` 時のみ）。Prefab はルートが 1 つなので単一オブジェクト
4. `game_objects`: フラット配列形式（`offset > 0` 時のみ）
5. 各ノードの `path`: Prefab ルートからの相対パス。ルート自身は `""`
6. 各ノードの `nested_prefab_asset_path`（optional）: ネスト Prefab インスタンスのルートの場合のみ付与
7. `next_offset`（optional）: `truncated=true` 時のみ

### 5.3 動作ルール

1. `prefab_path` の拡張子が `.prefab` でない場合は `ERR_INVALID_PARAMS`。
2. `AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path)` でロード。null なら `ERR_PREFAB_NOT_FOUND`。
3. `root_path` 省略/空文字列/`"/"` → Prefab ルートを起点。指定時は `PrefabGameObjectResolver.Resolve` で検索。見つからなければ `ERR_OBJECT_NOT_FOUND`。MCP レイヤーでは `root_path` を wire protocol の `game_object_path` にマッピング。
4. ネスト Prefab 判定: `PrefabUtility.IsAnyPrefabInstanceRoot(go) && go != prefabRoot`。
5. 走査ルール（幅優先、max_depth/max_game_objects）は Scene 仕様 §8.3 と同一。`component_filter` の動作も Scene 仕様 §8.3 と同一。
6. 読み取りは `AssetDatabase.LoadAssetAtPath` を使用（`LoadPrefabContents` は不要）。
7. Unity メインスレッドで実行。

### 5.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |


---

## 6. `get_component_info` — Prefab モード (read-only)

### 6.1 入力仕様

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
      "description": "0-based component index from get_hierarchy. When omitted, returns a lightweight list of all components with their indices."
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
      "default": 16,
      "description": "Maximum number of array/List elements to expand per field."
    }
  },
  "required": ["prefab_path", "game_object_path"],
  "additionalProperties": false
}
```

### 6.2 出力仕様

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

フィールド値のフォーマットは Scene 仕様 §3.1 の Field Value Format に従う。参照の判別は §3.2 のロジックを使用。

#### index 省略時（コンポーネント一覧モード）

```json
{
  "prefab_path": "Assets/Prefabs/Player.prefab",
  "game_object_path": "",
  "game_object_name": "Player",
  "mode": "list",
  "components": [
    { "index": 0, "component_type": "UnityEngine.Transform" },
    { "index": 1, "component_type": "UnityEngine.Animator" },
    { "index": 2, "component_type": "MyGame.PlayerController" }
  ],
  "count": 3
}
```

### 6.3 動作ルール

1. `.prefab` 拡張子チェック → `ERR_INVALID_PARAMS`。
2. `AssetDatabase.LoadAssetAtPath<GameObject>` でロード → null なら `ERR_PREFAB_NOT_FOUND`。
3. `game_object_path` で検索（§4 PrefabGameObjectResolver）→ 見つからなければ `ERR_OBJECT_NOT_FOUND`。
4. `index` でコンポーネント取得 → 範囲外は `ERR_COMPONENT_INDEX_OUT_OF_RANGE`、null は `ERR_MISSING_SCRIPT`。
5. `FieldSerializer` でシリアライズ。`ref_path` は `PrefabGameObjectResolver.GetRelativePath` で算出。
6. Unity メインスレッドで実行。

### 6.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |


---

## 7. `manage_component` — Prefab モード (edit)

### 7.1 入力仕様

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
      "description": "Operation to perform. add: requires component_type. update: requires index+fields. remove: requires index. move: requires index+new_index."
    },
    "game_object_path": {
      "type": "string",
      "description": "Path within the Prefab. \"\" or \"/\" = root."
    },
    "component_type": {
      "type": "string",
      "description": "Fully qualified or simple name of the component type to add. Required for 'add' action."
    },
    "index": {
      "type": "integer",
      "minimum": 0,
      "description": "0-based component index. Required for 'update'/'remove'/'move'. Optional for 'add' to specify insertion position."
    },
    "new_index": {
      "type": "integer",
      "minimum": 0,
      "description": "Target position for 'move' action. Required for 'move' only."
    },
    "fields": {
      "type": "object",
      "description": "Serialized field name-value map. For 'add' and 'update'.",
      "additionalProperties": true
    }
  },
  "required": ["prefab_path", "action", "game_object_path"],
  "additionalProperties": false
}
```

action ごとの必須パラメータは Scene 仕様 §10.1 と同一。

### 7.2 出力仕様

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

### 7.3 動作ルール

#### 共通
1. **Play Mode ガード: なし**。
2. `.prefab` 拡張子チェック → `ERR_INVALID_PARAMS`。
3. `PrefabUtility.LoadPrefabContents(prefab_path)` でロード → null なら `ERR_PREFAB_NOT_FOUND`。
4. `PrefabGameObjectResolver.Resolve(root, game_object_path)` で GO を検索 → 見つからなければ `ERR_OBJECT_NOT_FOUND`（`UnloadPrefabContents` で破棄）。

#### 事前検証による原子性（save-or-discard）

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

#### `add`
Scene 仕様 §10.3 の `add` と同一。`Undo.AddComponent` → `go.AddComponent(type)` に置換。

#### `update`
Scene 仕様 §10.3 の `update` と同一。Undo 省略。

#### `remove`
Scene 仕様 §10.3 の `remove` と同一。`Undo.DestroyObjectImmediate` → `Object.DestroyImmediate` に置換。`[RequireComponent]` チェックは再利用。

#### `move`
Scene 仕様 §10.3 の `move` と同一。`ComponentUtility.MoveComponentUp/Down` で移動。Undo 省略。

#### フィールド設定（`add`/`update` 共通）
Scene 仕様 §10.3 と同一（`FieldDeserializer.Apply` を再利用）。差分:
- `ApplyModifiedProperties()` → `ApplyModifiedPropertiesWithoutUndo()`
- `$ref` 解決: `GameObjectResolver.Resolve(path)` → `PrefabGameObjectResolver.Resolve(prefabRoot, path)` に差し替え

### 7.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |


---

## 8. `find_game_objects` — Prefab モード (read-only)

### 8.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "prefab_path": {
      "type": "string",
      "description": "Asset path of the Prefab (e.g. \"Assets/Prefabs/Player.prefab\")."
    },
    "name": {
      "type": "string",
      "description": "Name filter (regex, case-insensitive)."
    },
    "tag": {
      "type": "string",
      "description": "Tag filter (exact match)."
    },
    "component_type": {
      "type": "string",
      "description": "Component type name to filter by."
    },
    "root_path": {
      "type": "string",
      "description": "Path within the Prefab to limit the search scope."
    },
    "layer": {
      "type": "integer",
      "minimum": 0,
      "maximum": 31,
      "description": "Layer index to filter by (exact match)."
    },
    "active": {
      "type": "boolean",
      "description": "Filter by active state."
    },
    "max_results": {
      "type": "integer",
      "minimum": 1,
      "maximum": 1000,
      "default": 100,
      "description": "Maximum number of results to return."
    },
    "offset": {
      "type": "integer",
      "minimum": 0,
      "default": 0,
      "description": "Number of results to skip. Use 'next_offset' from a truncated response to fetch the next page."
    }
  },
  "required": ["prefab_path"],
  "additionalProperties": false
}
```

`name`, `tag`, `component_type`, `layer` の少なくとも 1 つ必須（プライマリフィルタ）。

### 8.2 出力仕様

```json
{
  "game_objects": [
    {
      "name": "Model",
      "path": "/Model",
      "tag": "Untagged",
      "layer": 0,
      "active": true,
      "components": ["Transform", "MeshRenderer", "MeshFilter"]
    }
  ],
  "count": 1,
  "truncated": false
}
```

追加フィールド:
- `next_offset` (int, optional): `truncated=true` 時のみ

### 8.3 動作ルール

1. `AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path)` で Prefab をロード。見つからなければ `ERR_OBJECT_NOT_FOUND`。
2. `root_path` 指定時は `PrefabGameObjectResolver.Resolve(prefabAsset, rootPath)` で起点を特定。
3. 起点から再帰的に全 GO を走査し、全フィルタ条件を AND で適用。
4. `name`: 正規表現マッチ（大文字小文字無視）。不正な正規表現は `ERR_INVALID_PARAMS`。
5. `tag`: `CompareTag()` による完全一致。
6. `component_type`: `ComponentTypeResolver` で型を解決し、`GetComponent(type)` で存在チェック。
7. `layer`: `go.layer == layerFilter` による完全一致。
8. `active`: **`go.activeSelf`** による一致（Scene 版の `activeInHierarchy` と異なる）。
9. `offset` と `max_results` でページネーション。
10. パスは `PrefabGameObjectResolver.GetRelativePath` で算出。

### 8.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |


---

## 9. `manage_game_object` — Prefab モード (edit)

### 9.1 入力仕様

`manage_game_object` Scene モード（Scene 仕様 §12.1）と同一スキーマに `prefab_path` (required) を追加。`game_object_path` と `parent_path` は Prefab ルートからの相対パス。

```json
{
  "type": "object",
  "properties": {
    "prefab_path": {
      "type": "string",
      "description": "Asset path of the Prefab (e.g. \"Assets/Prefabs/Player.prefab\")."
    },
    "action": {
      "type": "string",
      "enum": ["create", "update", "delete", "reparent"],
      "description": "Operation to perform. create: new GameObject. update: modify name/tag/layer/active. delete: destroy with children. reparent: move to new parent."
    },
    "game_object_path": {
      "type": "string",
      "description": "Path within the Prefab of the target GameObject. Required for update/delete/reparent."
    },
    "parent_path": {
      "type": "string",
      "description": "Parent path within the Prefab. For create: optional (omit for prefab root). For reparent: new parent path (omit for prefab root)."
    },
    "name": {
      "type": "string",
      "description": "Name of the GameObject. Required for create. For update: renames."
    },
    "tag": {
      "type": "string",
      "description": "Tag to assign. For create/update."
    },
    "layer": {
      "type": "integer",
      "minimum": 0,
      "maximum": 31,
      "description": "Layer index. For create/update."
    },
    "active": {
      "type": "boolean",
      "description": "Active state. For create/update. Default: true for create."
    },
    "primitive_type": {
      "type": "string",
      "enum": ["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"],
      "description": "Creates a Unity primitive. For create only."
    },
    "world_position_stays": {
      "type": "boolean",
      "default": true,
      "description": "Preserve world position during reparent."
    },
    "sibling_index": {
      "type": "integer",
      "minimum": 0,
      "description": "Position among siblings. For create/reparent."
    }
  },
  "required": ["prefab_path", "action"],
  "additionalProperties": false
}
```

### 9.2 出力仕様

Scene 仕様 §12.3 と同一構造 + `prefab_path` フィールド。

### 9.3 動作ルール

1. **Play Mode ガード: なし**。
2. `LoadPrefabContents` / `SaveAsPrefabAsset` / `UnloadPrefabContents` パターンで書き込み（Undo なし）。
3. `parent_path` 省略時は Prefab ルートが親となる。
4. **Prefab ルート自体の delete/reparent はエラー**。
5. `try-finally` で `UnloadPrefabContents` を保証。
6. その他の動作（create/update/delete/reparent のロジック）は Scene 仕様 §12.4 と同一。

### 9.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |


---

## 10. エラー仕様

| エラーコード | 発生条件 |
|---|---|
| `ERR_INVALID_PARAMS` | パラメータ不正、拡張子が `.prefab` でない、不正な正規表現パターン |
| `ERR_PREFAB_NOT_FOUND` | Prefab アセットが見つからない |
| `ERR_OBJECT_NOT_FOUND` | `game_object_path` / `root_path` / `parent_path` が Prefab 内に見つからない |
| `ERR_COMPONENT_TYPE_NOT_FOUND` | コンポーネント型が解決できない |
| `ERR_COMPONENT_TYPE_AMBIGUOUS` | コンポーネント型が複数の型に一致 |
| `ERR_INVALID_COMPONENT_TYPE` | `Component` 非継承の型を指定 |
| `ERR_COMPONENT_INDEX_OUT_OF_RANGE` | コンポーネント index が範囲外 |
| `ERR_MISSING_SCRIPT` | Missing Script（null コンポーネント） |
| `ERR_REFERENCE_NOT_FOUND` | `$ref` / `$asset` 参照先なし |
| `ERR_COMPONENT_DEPENDENCY` | `[RequireComponent]` 依存による削除拒否 |
| `ERR_PREFAB_SAVE_FAILED` | `SaveAsPrefabAsset` 失敗 |
| `ERR_CIRCULAR_HIERARCHY` | 自分自身 / 先祖への reparent |
| `ERR_INVALID_TAG` | 未定義タグの指定 |

---

## 11. 実装参照ファイル

### Server

| ファイル | 内容 |
|---|---|
| `Server/ToolCatalog.cs` | Scene/Prefab 統合ツール含む全スキーマ・メタデータ定義 |
| `Server/ToolContracts.cs` | `ToolNames`, Request/Result records |

### Unity Plugin

| ファイル | 内容 |
|---|---|
| `UnityMCPPlugin/.../Tools/PrefabHierarchyTool.cs` | get_prefab_hierarchy 実装 |
| `UnityMCPPlugin/.../Tools/PrefabComponentInfoTool.cs` | get_prefab_component_info 実装 |
| `UnityMCPPlugin/.../Tools/ManagePrefabComponentTool.cs` | manage_prefab_component 実装 |
| `UnityMCPPlugin/.../Tools/FindPrefabGameObjectsTool.cs` | find_prefab_game_objects 実装 |
| `UnityMCPPlugin/.../Tools/ManagePrefabGameObjectTool.cs` | manage_prefab_game_object 実装 |
| `UnityMCPPlugin/.../Tools/PrefabGameObjectResolver.cs` | Prefab 内パス解決 |
| `UnityMCPPlugin/.../Tools/PrefabToolContracts.cs` | Prefab ツール固有の型定義 |
