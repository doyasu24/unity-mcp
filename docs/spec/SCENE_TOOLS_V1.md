# Scene Tools 仕様 v1

- Status: Draft
- Date: 2026-03-02
- Target: `Server` / `UnityMCPPlugin`

## 1. 目的

MCP からシーン管理（一覧・開く・保存・作成）、シーン階層の読み取りとコンポーネント操作、GameObject の検索・管理・Prefab インスタンス化を行えるようにする。

LLM のワークフロー:
1. `list_scenes` でプロジェクト内のシーン一覧を確認
2. `open_scene` / `create_scene` でシーンを切り替え・作成
3. `get_hierarchy` で Scene 全体の構造を把握する（`prefab_path` 省略 = Scene モード）
4. `get_component_info` で対象コンポーネントのフィールド詳細を確認する
5. `manage_component` でコンポーネントの追加・更新・削除・並べ替えを行う
6. `manage_game_object` で GameObject の作成・更新・削除・親子関係変更を行う
7. `find_game_objects` で条件に合う GameObject を検索する
8. `instantiate_prefab` で Prefab をシーンに配置する
9. `save_scene` で変更を保存

## 2. ツール一覧

| # | ツール名 | 種別 | timeout(ms) | retryable |
|---|---|---|---|---|
| 1 | `list_scenes` | read-only | 5000/10000 | true |
| 2 | `open_scene` | edit | 30000/60000 | false |
| 3 | `save_scene` | edit | 30000/60000 | false |
| 4 | `create_scene` | edit | 30000/60000 | false |
| 5 | `get_hierarchy` | read-only | 10000/30000 | true |
| 6 | `get_component_info` | read-only | 10000/30000 | true |
| 7 | `manage_component` | edit | 10000/30000 | false |
| 8 | `find_game_objects` | read-only | 10000/30000 | true |
| 9 | `manage_game_object` | edit | 10000/30000 | false |
| 10 | `instantiate_prefab` | edit | 10000/30000 | false |

timeout 列は `default_timeout_ms / max_timeout_ms`。

---

## 3. 共通概念

### 3.1 Field Value Format

`get_component_info`（Scene / Prefab 共通） が返すフィールド値のフォーマット。

**プリミティブ型:**
- `int`, `float`, `bool`, `string`, `enum` → JSON のネイティブ型で返す
- `Vector2/3/4` → `{ "x": float, "y": float, ... }`
- `Color` → `{ "r": float, "g": float, "b": float, "a": float }`
- `Quaternion` → `{ "x": float, "y": float, "z": float, "w": float }`

**参照型:**
- Scene 内オブジェクト参照 → `{ "type": "ComponentType", "value": "Name (Type)", "is_object_ref": true, "ref_path": "/Path/To/GO" }`
- アセット参照 → `{ "type": "AssetType", "value": "Name (Type)", "is_asset_ref": true, "asset_path": "Assets/..." }`
- null 参照 → `{ "type": "ExpectedType", "value": null }`

**配列型:**
- `max_array_elements` で展開数を制限（デフォルト 16、最大 64）
- `0` 指定時は要素数のみ返す

### 3.2 FieldSerializer / FieldDeserializer

- `FieldSerializer`: コンポーネントの `SerializedProperty` を JSON に変換する（読み取り時）
- `FieldDeserializer`: JSON の値を `SerializedProperty` に設定する（書き込み時）
- `$ref` 記法: Scene 内の GameObject/コンポーネントへの参照を指定する → `{ "$ref": "/Path/To/GO", "component": "TypeName" }`
- `$asset` 記法: アセットへの参照を指定する → `{ "$asset": "Assets/Materials/Default.mat" }`

### 3.3 ComponentTypeResolver

コンポーネント型名の解決ロジック:
1. 完全修飾名で検索（例: `UnityEngine.UI.Image`）
2. 単純名で検索（例: `Rigidbody`）
3. 複数一致 → `ERR_COMPONENT_TYPE_AMBIGUOUS`
4. 未発見 → `ERR_COMPONENT_TYPE_NOT_FOUND`
5. `Component` 非継承 → `ERR_INVALID_COMPONENT_TYPE`

### 3.4 offset ベースページネーション

`get_hierarchy` と `find_game_objects` で使用（Scene / Prefab 共通）。

- `offset` パラメータでスキップ数を指定（デフォルト 0）
- レスポンスに `truncated: true` と `next_offset` を含めて次ページを案内
- `get_hierarchy` で `offset > 0` の場合、ネストツリーではなくフラット配列 `game_objects` で返す

---

## 4. `list_scenes` (read-only)

### 4.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "name_pattern": {
      "type": "string",
      "description": "Filter scenes by name (regex, case-insensitive). Applied to the file name without extension."
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
  "additionalProperties": false
}
```

### 4.2 出力仕様

```json
{
  "scenes": [
    { "path": "Assets/Scenes/SampleScene.unity" }
  ],
  "count": 1,
  "total_count": 5,
  "truncated": false
}
```

追加フィールド:
- `total_count` (int): フィルタ適用後の全件数
- `truncated` (bool): `max_results` を超えた場合 `true`
- `next_offset` (int, optional): `truncated=true` 時のみ。次ページ取得時に `offset` に渡す値

### 4.3 動作ルール

1. `AssetDatabase.FindAssets("t:Scene")` で全シーンアセットの GUID を取得し、`GUIDToAssetPath` でパスに変換する。
2. `name_pattern` 指定時はファイル名（拡張子なし）に対して正規表現フィルタ（case-insensitive）を適用する。不正な正規表現パターンは `ERR_INVALID_PARAMS`。
3. フィルタ後の結果を `path` でソートし、ページネーション順序を安定化する。
4. `offset` で指定された数をスキップし、以降の `max_results` 件を返す。残りがある場合は `truncated=true` と `next_offset` を設定。

### 4.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `5000` |
| `max_timeout_ms` | `10000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `true` |

---

## 5. `open_scene` (edit)

### 5.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "Asset path of the scene file (e.g. \"Assets/Scenes/Main.unity\")."
    },
    "mode": {
      "type": "string",
      "enum": ["single", "additive"],
      "default": "single",
      "description": "How to open the scene. 'single' replaces current scene, 'additive' adds to current scene."
    }
  },
  "required": ["path"],
  "additionalProperties": false
}
```

### 5.2 出力仕様

```json
{
  "path": "Assets/Scenes/Main.unity",
  "mode": "single"
}
```

### 5.3 動作ルール

1. 現在のアクティブシーンに未保存変更がある場合は `ERR_UNSAVED_CHANGES` を返す（LLM が事前に `save_scene` を呼ぶべき）。
2. `EditorSceneManager.OpenScene(path, OpenSceneMode)` でシーンを開く。
3. `mode=single`: 現在のシーンを置き換え。`mode=additive`: 追加で開く。

### 5.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `30000` |
| `max_timeout_ms` | `60000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `false` |

---

## 6. `save_scene` (edit)

### 6.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "Asset path of a specific open scene to save. If omitted, saves the active scene."
    }
  },
  "additionalProperties": false
}
```

### 6.2 出力仕様

```json
{
  "path": "Assets/Scenes/Main.unity"
}
```

### 6.3 動作ルール

1. `path` 指定時: 開いているシーンの中から一致するものを探し、`EditorSceneManager.SaveScene(scene)` で保存。見つからない場合は `ERR_OBJECT_NOT_FOUND`。
2. `path` 省略時: `SceneManager.GetActiveScene()` を保存。

### 6.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `30000` |
| `max_timeout_ms` | `60000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `false` |

---

## 7. `create_scene` (edit)

### 7.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "Asset path where the new scene will be saved (e.g. \"Assets/Scenes/NewScene.unity\")."
    },
    "setup": {
      "type": "string",
      "enum": ["default", "empty"],
      "default": "default",
      "description": "'default' includes a camera and light. 'empty' creates a blank scene."
    }
  },
  "required": ["path"],
  "additionalProperties": false
}
```

### 7.2 出力仕様

```json
{
  "path": "Assets/Scenes/NewScene.unity"
}
```

### 7.3 動作ルール

1. `EditorSceneManager.NewScene(setup, NewSceneMode.Single)` で新規シーンを作成。
2. `setup=default`: Camera + Light 付き。`setup=empty`: 空のシーン。
3. 作成後、`EditorSceneManager.SaveScene(scene, path)` で指定パスに保存。

### 7.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `30000` |
| `max_timeout_ms` | `60000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `false` |

---

## 8. `get_hierarchy` — Scene モード (read-only)

### 8.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "root_path": {
      "type": "string",
      "description": "Optional: hierarchy path of a root GameObject to start from. If omitted, returns the entire scene."
    },
    "max_depth": {
      "type": "integer",
      "minimum": 0,
      "maximum": 50,
      "default": 10,
      "description": "Maximum depth of the hierarchy tree to traverse. 0 returns only the root level."
    },
    "max_game_objects": {
      "type": "integer",
      "minimum": 1,
      "maximum": 10000,
      "default": 1000,
      "description": "Maximum number of GameObjects to include in the response. When exceeded, the response is truncated and 'truncated' is set to true. Use 'root_path' to drill into a specific subtree."
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
      "description": "Only return GameObjects that have at least one of these component types. Matches Type.Name or Type.FullName (case-insensitive). In tree mode (offset=0), non-matching GOs keep structure but omit components. In flat mode (offset>0), non-matching GOs are excluded."
    }
  },
  "additionalProperties": false
}
```

### 8.2 出力仕様

#### offset=0（デフォルト）: ネストツリー形式

```json
{
  "scene_name": "SampleScene",
  "root_game_objects": [
    {
      "name": "Main Camera",
      "path": "/Main Camera",
      "active": true,
      "components": ["Transform", "Camera", "AudioListener"],
      "children": []
    },
    {
      "name": "Canvas",
      "path": "/Canvas",
      "active": true,
      "components": ["RectTransform", "Canvas", "CanvasScaler", "GraphicRaycaster"],
      "children": [
        {
          "name": "Panel",
          "path": "/Canvas/Panel",
          "active": true,
          "components": ["RectTransform", "Image"],
          "children": []
        }
      ]
    }
  ],
  "total_game_objects": 3,
  "truncated": false
}
```

#### offset > 0: フラット配列形式

```json
{
  "scene_name": "SampleScene",
  "game_objects": [
    { "name": "Panel", "path": "/Canvas/Panel", "active": true, "components": ["RectTransform", "Image"] }
  ],
  "total_game_objects": 1,
  "truncated": true,
  "next_offset": 5
}
```

フィールド定義:
1. `scene_name`: アクティブシーンの名前
2. `root_game_objects`: ルート階層の GameObject 配列（ツリー形式、`offset=0` 時のみ）
3. `game_objects`: フラット配列形式（`offset > 0` 時のみ）
4. 各ノードの `path`: Scene hierarchy 絶対パス（例: `"/Canvas/Panel"`）
5. 各ノードの `components`: コンポーネント型名の配列（index 順）
6. `total_game_objects`: レスポンスに含まれる GO 数
7. `truncated`: `max_game_objects` を超えた場合 `true`
8. `next_offset`（optional）: `truncated=true` 時のみ。次ページ取得時に `offset` に渡す値

### 8.3 動作ルール

1. `root_path` 指定時は `GameObjectResolver.Resolve(rootPath)` で起点を特定。見つからなければ `ERR_OBJECT_NOT_FOUND`。
2. `root_path` 省略時はアクティブシーンの全ルート GO を起点とする。
3. 幅優先で走査し、`max_depth` と `max_game_objects` で制限する。
4. `offset > 0` の場合はフラット配列 `game_objects` で返す。`offset=0` の場合はネストツリー `root_game_objects` で返す。
5. `component_filter` 指定時の動作:
   - **ツリーモード（offset=0）**: ツリー構造を維持しつつ、フィルタにマッチしない GO の `components` 配列を省略してトークン削減。`max_game_objects` のカウント対象はマッチ GO のみ。
   - **フラットモード（offset>0）**: フィルタにマッチした GO のみ返す。マッチ判定は `Type.Name` または `Type.FullName` の完全一致（case-insensitive）。
6. Unity メインスレッドで実行する。

### 8.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `true` |

---

## 9. `get_component_info` — Scene モード (read-only)

### 9.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "game_object_path": {
      "type": "string",
      "description": "Scene hierarchy path of the target GameObject (e.g. \"/Canvas/Panel\" or \"Main Camera\")."
    },
    "index": {
      "type": "integer",
      "minimum": 0,
      "description": "0-based index of the component on the GameObject. Corresponds to the index from get_hierarchy output."
    },
    "fields": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Optional list of field names to return. When specified, only these fields are included in the response. When omitted, all serialized fields are returned."
    },
    "max_array_elements": {
      "type": "integer",
      "minimum": 0,
      "maximum": 64,
      "default": 16,
      "description": "Maximum number of array/List elements to expand per field. Elements beyond this limit are truncated. 0 returns element count only."
    }
  },
  "required": ["game_object_path", "index"],
  "additionalProperties": false
}
```

### 9.2 出力仕様

```json
{
  "game_object_path": "/Canvas/Panel",
  "game_object_name": "Panel",
  "index": 1,
  "component_type": "UnityEngine.UI.Image",
  "fields": {
    "m_Color": { "r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0 },
    "m_Material": {
      "type": "UnityEngine.Material",
      "value": "Default UI Material (Material)",
      "is_asset_ref": true,
      "asset_path": "Assets/Materials/DefaultUI.mat"
    },
    "m_Sprite": {
      "type": "UnityEngine.Sprite",
      "value": null
    }
  }
}
```

フィールド値のフォーマットは §3.1 の Field Value Format に従う。

### 9.3 動作ルール

1. `GameObjectResolver.Resolve(game_object_path)` で GO を取得。見つからなければ `ERR_OBJECT_NOT_FOUND`。
2. `index` でコンポーネント取得。範囲外は `ERR_COMPONENT_INDEX_OUT_OF_RANGE`、null は `ERR_MISSING_SCRIPT`。
3. `FieldSerializer` でシリアライズ。
4. `fields` 指定時は指定フィールドのみ返す。
5. 参照の判別: `EditorUtility.IsPersistent(obj)` が `false` → Scene 内参照（`is_object_ref`）、`true` → アセット参照（`is_asset_ref`）。
6. Unity メインスレッドで実行する。

### 9.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `true` |

---

## 10. `manage_component` — Scene モード (edit)

### 10.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "action": {
      "type": "string",
      "enum": ["add", "update", "remove", "move"],
      "description": "Operation to perform. 'add': requires component_type. 'update': requires index and fields. 'remove': requires index. 'move': requires index and new_index."
    },
    "game_object_path": {
      "type": "string",
      "description": "Scene hierarchy path of the target GameObject (e.g. \"/Canvas/Panel/Button\" or \"Main Camera\"). Root objects can omit the leading slash."
    },
    "component_type": {
      "type": "string",
      "description": "Fully qualified or simple name of the component type to add (e.g. \"Rigidbody\", \"PlayerController\", \"UnityEngine.UI.Image\"). Required for 'add' action."
    },
    "index": {
      "type": "integer",
      "minimum": 0,
      "description": "0-based component index on the GameObject (matches get_hierarchy output). Required for 'update'/'remove'/'move' to identify the target component. Optional for 'add' to specify insertion position (must be >= 1 since index 0 is Transform; default: append to end)."
    },
    "new_index": {
      "type": "integer",
      "minimum": 0,
      "description": "Target position for 'move' action. Required for 'move' only."
    },
    "fields": {
      "type": "object",
      "description": "Key-value map of serialized field names to values. Applicable to 'add' and 'update' actions.",
      "additionalProperties": true
    }
  },
  "required": ["action", "game_object_path"],
  "additionalProperties": false
}
```

### 10.2 出力仕様

```json
{
  "action": "add",
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "component_type": "MyGame.PlayerController",
  "index": 3,
  "fields_set": ["speed", "weapon"],
  "fields_skipped": []
}
```

### 10.3 動作ルール

#### 共通
1. **Play Mode ガード**: Play Mode 中は `ERR_PLAY_MODE_ACTIVE` を返す。
2. `GameObjectResolver.Resolve(game_object_path)` で GO を取得。見つからなければ `ERR_OBJECT_NOT_FOUND`。
3. Unity Undo システムに統合する。

#### `add`
1. `ComponentTypeResolver.Resolve(component_type)` で型を解決する（§3.3 参照）。
2. `Undo.AddComponent(go, type)` でコンポーネントを追加。
3. `fields` が指定されている場合は `FieldDeserializer.Apply` でフィールド値を設定。
4. `$ref` は `GameObjectResolver.Resolve(path)` で Scene 内から解決。
5. `$asset` は `AssetDatabase.LoadAssetAtPath` で解決。

#### `update`
1. `index` でコンポーネント取得。範囲外は `ERR_COMPONENT_INDEX_OUT_OF_RANGE`。
2. `fields` の値を `FieldDeserializer.Apply` で設定。

#### `remove`
1. `index` でコンポーネント取得。
2. `[RequireComponent]` 依存チェック。依存がある場合は `ERR_COMPONENT_DEPENDENCY`。
3. `Undo.DestroyObjectImmediate(component)` で削除。

#### `move`
1. `index` と `new_index` でコンポーネントの並べ替え。
2. `ComponentUtility.MoveComponentUp/Down` で移動。

#### フィールド設定（`add`/`update` 共通）
1. `SerializedObject` / `SerializedProperty` 経由で値設定。
2. `$ref` 参照は事前検証（Phase 1: validate-then-apply）。
3. 1 件でも参照解決失敗 → `ERR_REFERENCE_NOT_FOUND` でロールバック。

### 10.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `false` |

---

## 11. `find_game_objects` — Scene モード (read-only)

### 11.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "name": {
      "type": "string",
      "description": "Name filter (regex pattern, case-insensitive)."
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
      "description": "Hierarchy path of a root GameObject to limit the search scope."
    },
    "layer": {
      "type": "integer",
      "minimum": 0,
      "maximum": 31,
      "description": "Layer index to filter by (exact match)."
    },
    "active": {
      "type": "boolean",
      "description": "Filter by activeInHierarchy state."
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
  "additionalProperties": false
}
```

`name`, `tag`, `component_type`, `layer` の少なくとも 1 つ必須（プライマリフィルタ）。`active` は修飾フィルタであり、単独では使用不可。

### 11.2 出力仕様

```json
{
  "game_objects": [
    {
      "name": "Main Camera",
      "path": "/Main Camera",
      "tag": "MainCamera",
      "layer": 0,
      "active": true,
      "components": ["Transform", "Camera", "AudioListener"]
    }
  ],
  "count": 1,
  "truncated": false
}
```

追加フィールド:
- `next_offset` (int, optional): `truncated=true` 時のみ。次ページ取得時に `offset` に渡す値

### 11.3 動作ルール

1. `root_path` 指定時は `GameObjectResolver.Resolve(rootPath)` で起点を特定。未指定時はアクティブシーンの全ルート GO。
2. 起点から再帰的に全 GO を走査し、全フィルタ条件を AND で適用。
3. `name`: 正規表現マッチ（大文字小文字無視、`Regex.IsMatch(go.name, pattern, RegexOptions.IgnoreCase)`）。不正な正規表現パターンは `ERR_INVALID_PARAMS`。
4. `tag`: `CompareTag()` による完全一致。
5. `component_type`: `ComponentTypeResolver` で型を解決し、`GetComponent(type)` で存在チェック。
6. `layer`: `go.layer == layerFilter` による完全一致。
7. `active`: `go.activeInHierarchy == activeFilter` による一致。
8. `offset` で指定された数のマッチをスキップし、以降の `max_results` 件を返す。残りがある場合は `truncated=true` と `next_offset` を設定。

### 11.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `true` |

---

## 12. `manage_game_object` — Scene モード (edit)

### 12.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "action": {
      "type": "string",
      "enum": ["create", "update", "delete", "reparent"],
      "description": "Operation to perform. 'create': creates a new GameObject. 'update': modifies name/tag/layer/active. 'delete': destroys the GameObject and all children. 'reparent': moves to a new parent."
    },
    "game_object_path": {
      "type": "string",
      "description": "Scene hierarchy path of the target GameObject. Required for update/delete/reparent."
    },
    "parent_path": {
      "type": "string",
      "description": "Parent GameObject path. For create: optional (omit for scene root). For reparent: new parent path (omit or null for scene root)."
    },
    "name": {
      "type": "string",
      "description": "Name of the GameObject. Required for create. Optional for update (renames the GO)."
    },
    "tag": {
      "type": "string",
      "description": "Tag to assign. Optional for create/update."
    },
    "layer": {
      "type": "integer",
      "minimum": 0,
      "maximum": 31,
      "description": "Layer index (0-31). Optional for create/update."
    },
    "active": {
      "type": "boolean",
      "description": "Active state. Optional for create/update. Default: true for create."
    },
    "primitive_type": {
      "type": "string",
      "enum": ["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"],
      "description": "Creates a Unity primitive. Optional for create only."
    },
    "world_position_stays": {
      "type": "boolean",
      "default": true,
      "description": "Preserve world position during reparent. Optional for reparent."
    },
    "sibling_index": {
      "type": "integer",
      "minimum": 0,
      "description": "Position among siblings. Optional for create/reparent."
    }
  },
  "required": ["action"],
  "additionalProperties": false
}
```

### 12.2 Action 別バリデーション

| Parameter | create | update | delete | reparent |
|---|---|---|---|---|
| `game_object_path` | - | required | required | required |
| `parent_path` | optional | - | - | optional (null=root) |
| `name` | required | optional | - | - |
| `tag` | optional | optional | - | - |
| `layer` | optional | optional | - | - |
| `active` | optional | optional | - | - |
| `primitive_type` | optional | - | - | - |
| `world_position_stays` | - | - | - | optional |
| `sibling_index` | optional | - | - | optional |

### 12.3 出力仕様

**create:**
```json
{
  "action": "create",
  "game_object_path": "/Parent/NewChild",
  "game_object_name": "NewChild",
  "parent_path": "/Parent",
  "primitive_type": "Cube"
}
```

**update:**
```json
{
  "action": "update",
  "game_object_path": "/Parent/RenamedChild",
  "game_object_name": "RenamedChild",
  "previous_path": "/Parent/OldName",
  "properties_set": ["name", "tag"]
}
```

**delete:**
```json
{
  "action": "delete",
  "game_object_path": "/Parent/Child",
  "game_object_name": "Child",
  "children_deleted": 3
}
```

**reparent:**
```json
{
  "action": "reparent",
  "game_object_path": "/NewParent/Child",
  "game_object_name": "Child",
  "previous_parent_path": "/OldParent",
  "new_parent_path": "/NewParent",
  "world_position_stays": true,
  "sibling_index": 0
}
```

### 12.4 動作ルール

1. **Play Mode ガード**: Play Mode 中は `ERR_PLAY_MODE_ACTIVE` を返す。
2. 全アクションが Unity Undo システムに統合される:
   - `create`: `Undo.RegisterCreatedObjectUndo` + `Undo.SetTransformParent`
   - `update`: `Undo.RecordObject`
   - `delete`: `Undo.DestroyObjectImmediate`
   - `reparent`: `Undo.SetTransformParent`
3. `primitive_type` 指定時は `GameObject.CreatePrimitive(type)` で生成。
4. `reparent` で自分自身または先祖への移動は `ERR_CIRCULAR_HIERARCHY`。
5. 未定義タグの指定は `ERR_INVALID_TAG`。

### 12.5 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `false` |

---

## 13. `instantiate_prefab` (edit)

### 13.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "prefab_path": {
      "type": "string",
      "description": "Asset path of the Prefab (e.g. \"Assets/Prefabs/Player.prefab\")."
    },
    "parent_path": {
      "type": "string",
      "description": "Scene hierarchy path of the parent GameObject. Omit for scene root."
    },
    "position": {
      "type": "object",
      "description": "World position {x, y, z}. Default: Prefab's original position.",
      "properties": {
        "x": { "type": "number" },
        "y": { "type": "number" },
        "z": { "type": "number" }
      },
      "additionalProperties": false
    },
    "rotation": {
      "type": "object",
      "description": "Euler angles {x, y, z}. Default: Prefab's original rotation.",
      "properties": {
        "x": { "type": "number" },
        "y": { "type": "number" },
        "z": { "type": "number" }
      },
      "additionalProperties": false
    },
    "name": {
      "type": "string",
      "description": "Override the instantiated GameObject name."
    },
    "sibling_index": {
      "type": "integer",
      "minimum": 0,
      "description": "Position among siblings."
    }
  },
  "required": ["prefab_path"],
  "additionalProperties": false
}
```

### 13.2 出力仕様

```json
{
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "prefab_path": "Assets/Prefabs/Player.prefab",
  "instance_id": 12345
}
```

### 13.3 動作ルール

1. **Play Mode ガード**: Play Mode 中は `ERR_PLAY_MODE_ACTIVE` を返す。
2. `AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path)` で Prefab をロード。見つからなければ `ERR_OBJECT_NOT_FOUND`。
3. `parent_path` 指定時は `GameObjectResolver.Resolve(parentPath)` で親を取得。見つからなければ `ERR_OBJECT_NOT_FOUND`。
4. `PrefabUtility.InstantiatePrefab(prefabAsset)` でインスタンス化（Prefab リンクを維持）。
5. `Undo.RegisterCreatedObjectUndo` で Undo に登録。
6. 親が指定されている場合は `Undo.SetTransformParent` で親子関係を設定。
7. `position`/`rotation` が指定されている場合は Transform に反映。
8. `name` が指定されている場合はインスタンス名を上書き。
9. `sibling_index` が指定されている場合は `SetSiblingIndex` で位置を設定。
10. Undo 操作をグループ化する。
11. `EditorSceneManager.SaveOpenScenes()` でシーンを保存。

### 13.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |
| `execution_error_retryable` | `false` |

---

## 14. エラー仕様

| エラーコード | 発生条件 |
|---|---|
| `ERR_INVALID_PARAMS` | 必須パラメータ欠落、enum 値不正、`max_results` 範囲外、不正な正規表現パターン |
| `ERR_UNSAVED_CHANGES` | `open_scene` 時に現シーンに未保存変更あり |
| `ERR_OBJECT_NOT_FOUND` | GO / 親 GO / Prefab / シーンが見つからない |
| `ERR_COMPONENT_TYPE_NOT_FOUND` | コンポーネント型が解決できない |
| `ERR_COMPONENT_TYPE_AMBIGUOUS` | コンポーネント型が複数の型に一致 |
| `ERR_INVALID_COMPONENT_TYPE` | `Component` 非継承の型を指定 |
| `ERR_COMPONENT_INDEX_OUT_OF_RANGE` | コンポーネント index が範囲外 |
| `ERR_MISSING_SCRIPT` | Missing Script（null コンポーネント） |
| `ERR_REFERENCE_NOT_FOUND` | `$ref` / `$asset` 参照先なし |
| `ERR_COMPONENT_DEPENDENCY` | `[RequireComponent]` 依存による削除拒否 |
| `ERR_PLAY_MODE_ACTIVE` | Play Mode 中の変更禁止 |
| `ERR_CIRCULAR_HIERARCHY` | 自分自身 / 先祖への reparent |
| `ERR_INVALID_TAG` | 未定義タグの指定 |

Server から MCP クライアントへのエラー契約は `ERR_UNITY_EXECUTION` を使用し、詳細は `details` に含める。

---

## 15. 実装参照ファイル

### Server

| ファイル | 内容 |
|---|---|
| `Server/ToolCatalog.cs` | Scene/Prefab 統合ツール含む全スキーマ・メタデータ定義 |
| `Server/ToolContracts.cs` | `ToolNames`, Request/Result records |

### Unity Plugin

| ファイル | 内容 |
|---|---|
| `UnityMCPPlugin/.../CommandExecutor.cs` | シーン管理ツールの実行ロジック（list_scenes, open_scene, save_scene, create_scene, find_assets） |
| `UnityMCPPlugin/.../Tools/SceneHierarchyTool.cs` | get_scene_hierarchy 実装 |
| `UnityMCPPlugin/.../Tools/ComponentInfoTool.cs` | get_scene_component_info 実装 |
| `UnityMCPPlugin/.../Tools/ManageComponentTool.cs` | manage_scene_component 実装 |
| `UnityMCPPlugin/.../Tools/FindGameObjectsTool.cs` | find_scene_game_objects 実装 |
| `UnityMCPPlugin/.../Tools/ManageGameObjectTool.cs` | manage_scene_game_object 実装 |
| `UnityMCPPlugin/.../Tools/InstantiatePrefabTool.cs` | instantiate_prefab 実装 |
| `UnityMCPPlugin/.../Tools/FieldSerializer.cs` | フィールドのシリアライズ |
| `UnityMCPPlugin/.../Tools/FieldDeserializer.cs` | フィールドのデシリアライズ |
| `UnityMCPPlugin/.../Tools/ComponentTypeResolver.cs` | コンポーネント型解決 |
| `UnityMCPPlugin/.../Tools/GameObjectResolver.cs` | Scene hierarchy パス解決 |
| `UnityMCPPlugin/.../Tools/ReferenceResolver.cs` | 参照解決 |
