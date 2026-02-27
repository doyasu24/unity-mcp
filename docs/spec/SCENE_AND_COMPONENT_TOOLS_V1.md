# Scene & Component Tools 仕様 v1

- Status: Draft
- Date: 2026-02-28
- Target: `Server` / `UnityMCPPlugin`

## 1. 目的
MCP から Scene の構造を把握し、GameObject にスクリプト（MonoBehaviour）をアタッチし、SerializeField の値（Scene 上のオブジェクト参照を含む）を設定できるようにする。

LLM のワークフロー:
1. `get_scene_hierarchy` で Scene 全体の構造を把握する（GO 名、パス、コンポーネント型名一覧）
2. `get_component_info` で対象コンポーネントのフィールド詳細を確認する（index で指定）
3. `manage_component` でコンポーネントの追加・更新・削除・並べ替えを行う

## 2. 追加する tool

| # | ツール名 | 種別 | execution_mode | 役割 |
|---|---|---|---|---|
| 1 | `get_scene_hierarchy` | read-only | sync | Scene 全体の木構造を返す（GO 名、パス、コンポーネント型名一覧） |
| 2 | `get_component_info` | read-only | sync | 特定 GO のコンポーネント（index 指定）のフィールド詳細を返す |
| 3 | `manage_component` | edit | sync | コンポーネントの追加・更新・削除・並べ替え |

---

## 3. `get_scene_hierarchy` (read-only)

### 3.1 入力仕様

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
    }
  },
  "additionalProperties": false
}
```

### 3.2 出力仕様

```json
{
  "scene_name": "SampleScene",
  "scene_path": "Assets/Scenes/SampleScene.unity",
  "root_game_objects": [
    {
      "name": "Main Camera",
      "path": "/Main Camera",
      "active": true,
      "components": ["UnityEngine.Transform", "UnityEngine.Camera", "UnityEngine.AudioListener"],
      "children": []
    },
    {
      "name": "Canvas",
      "path": "/Canvas",
      "active": true,
      "prefab_asset_path": "Assets/Prefabs/UI/Canvas.prefab",
      "components": ["UnityEngine.Transform", "UnityEngine.Canvas", "UnityEngine.UI.CanvasScaler", "UnityEngine.UI.GraphicRaycaster"],
      "children": [
        {
          "name": "Panel",
          "path": "/Canvas/Panel",
          "active": true,
          "components": ["UnityEngine.Transform", "UnityEngine.UI.Image"],
          "children": [
            {
              "name": "HealthBar",
              "path": "/Canvas/Panel/HealthBar",
              "active": true,
              "components": ["UnityEngine.Transform", "UnityEngine.UI.Slider"],
              "children": "..."
            }
          ]
        }
      ]
    }
  ],
  "total_game_objects": 42,
  "truncated": false
}
```

フィールド定義:
1. `scene_name`: アクティブ Scene 名
2. `scene_path`: Scene アセットパス
3. `root_game_objects`: ルート GameObject の配列（再帰的に `children` を含む）
4. 各ノードの `name`: GameObject 名
5. 各ノードの `path`: Scene hierarchy の絶対パス（先頭 `/`）
6. 各ノードの `active`: `GameObject.activeSelf`
7. 各ノードの `prefab_asset_path`（optional）: Prefab instance root の場合のみ、ソース Prefab のアセットパス。Prefab instance の子ノードや非 Prefab の GO には含まない
8. 各ノードの `components`: アタッチされたコンポーネントの完全修飾型名（`Type.FullName`）リスト（文字列配列）。フィールド値は含まない。配列の位置（0-based）がそのまま `get_component_info` の `index` に対応する。Missing Script（スクリプトが欠損したコンポーネント）は `null` を返す
9. 各ノードの `children`: 子 GameObject の配列。`max_depth` に達した場合は `"..."` 文字列
10. `total_game_objects`: 走査した GameObject 総数
11. `truncated`: `max_depth` または `max_game_objects` により省略されたノードがある場合 `true`

### 3.3 動作ルール
1. `root_path` が指定された場合、そのパスの GameObject を起点として子孫を返す。
2. `root_path` が省略された場合、アクティブ Scene の全ルート GameObject を起点とする。
3. `root_path` の GameObject が見つからない場合は `ERR_OBJECT_NOT_FOUND` を返す。
4. 各ノードの `components` はアタッチ順で型名のみを返す（軽量化のため値は含まない）。Missing Script（`GetComponents<Component>()` が null を返す要素）は文字列 `null` として配列に含め、index の整合性を維持する。
5. `PrefabUtility.IsAnyPrefabInstanceRoot(go)` が `true` の場合、`prefab_asset_path` に `PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go)` を設定する。Prefab instance の子ノードには付与しない。
6. 非アクティブな GameObject も含める（`active` フィールドで判別可能）。
7. `max_depth` に達した子ノードは `children: "..."` として深さ制限を示す。
8. `max_game_objects` に達した場合、それ以降の GameObject は出力しない。`truncated: true` を設定する。走査は幅優先順（兄弟 → 子）で行い、上限到達時に打ち切る。
9. 実処理は Unity メインスレッドで実行する。

### 3.4 メタデータ
1. `execution_mode`: `sync`
2. `supports_cancel`: `false`
3. `default_timeout_ms`: `10000`
4. `max_timeout_ms`: `30000`
5. `requires_client_request_id`: `false`
6. `execution_error_retryable`: `true`

### 3.5 エラー仕様
| エラーコード | 発生条件 | retryable |
|---|---|---|
| `ERR_OBJECT_NOT_FOUND` | `root_path` の GameObject が見つからない | false |
| `ERR_INVALID_PARAMS` | パラメータ型不正 | false |

※ Server → MCP クライアントへのエラー伝搬は 5.6 節の二層構造に従う。

---

## 4. `get_component_info` (read-only)

### 4.1 入力仕様

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
      "description": "0-based index of the component on the GameObject. Corresponds to the index from get_scene_hierarchy output."
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

### 4.2 出力仕様

```json
{
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "index": 3,
  "component_type": "MyGame.EnemyChaser",
  "fields": {
    "chaseSpeed": 5.0,
    "maxSpeed": 20.0,
    "isActive": true,
    "playerName": "Hero",
    "target": {
      "type": "UnityEngine.GameObject",
      "value": "Enemy",
      "is_object_ref": true,
      "ref_path": "/Enemy"
    },
    "spawnPoint": {
      "type": "UnityEngine.Transform",
      "value": "Point1 (Transform)",
      "is_object_ref": true,
      "ref_path": "/SpawnPoints/Point1"
    },
    "alertMaterial": {
      "type": "UnityEngine.Material",
      "value": "Alert (Material)",
      "is_asset_ref": true,
      "asset_path": "Assets/Materials/Alert.mat"
    },
    "mode": { "type": "MyGame.ChaseMode", "value": "Aggressive" },
    "waypoints": {
      "type": "UnityEngine.Transform[]",
      "value": [
        { "value": "WP1 (Transform)", "is_object_ref": true, "ref_path": "/Waypoints/WP1" },
        { "value": "WP2 (Transform)", "is_object_ref": true, "ref_path": "/Waypoints/WP2" },
        { "value": "WP3 (Transform)", "is_object_ref": true, "ref_path": "/Waypoints/WP3" }
      ]
    }
  }
}
```

フィールド定義:
1. `game_object_path`: 対象 GameObject の Scene hierarchy パス
2. `game_object_name`: 対象 GameObject の名前
3. `index`: コンポーネントの index
4. `component_type`: コンポーネントの完全修飾型名（`Type.FullName`、index から解決）
5. `fields`: SerializedField の辞書。フィールド値は型に応じて 2 形式:
   - **プリミティブ型**（int, float, bool, string）→ JSON プリミティブを直接格納（ラッパーなし）
   - **それ以外**（Unity 値型、Enum、参照型、配列、[Serializable] 構造体）→ オブジェクトでラップ:
     - `type`: C# 完全修飾型名（`Type.FullName`）
     - `value`: 現在値（後述のシリアライズルールに従う）
     - `is_object_ref`: Scene 上のオブジェクト参照の場合 `true`
     - `ref_path`: 参照先の Scene hierarchy パス（取得可能な場合）
     - `is_asset_ref`: Asset 参照の場合 `true`
     - `asset_path`: AssetDatabase 上のパス

### 4.3 フィールド値のシリアライズルール

`SerializedObject` / `SerializedProperty` API を使用してフィールドを列挙する。

1. **対象メンバーの列挙**:
   - `SerializedObject` / `SerializedProperty` を利用して Unity が認識するフィールドを列挙する
   - `[SerializeField]` が付与された private/protected フィールド
   - public フィールド（`[NonSerialized]` でないもの）
   - **除外する内部プロパティ**: `m_Script`, `m_ObjectHideFlags`, `m_EditorHideFlags`, `m_EditorClassIdentifier`, `m_Name`（これらは Unity 内部管理用であり LLM 操作の対象外）
   - **`m_Enabled` は含める**: `Behaviour` 派生コンポーネントの有効/無効を制御するプロパティ。`update` action で `{"m_Enabled": false}` として設定可能
2. **値のシリアライズ**（プリミティブはフラット、それ以外は `{type, value, ...}` ラッパー）:
   - プリミティブ型（int, float, bool, string 等）→ JSON プリミティブを直接格納（**ラッパーなし**）
   - Unity 値型（Vector2/3/4, Vector2Int/3Int, Quaternion, Color, Rect, RectInt, Bounds, BoundsInt, LayerMask）→ `{type, value}` ラッパー付き成分オブジェクト（詳細は 5.4.9 節）
   - `AnimationCurve` → `{type, value}` ラッパー付きサマリー（`keyCount`, `timeRange`）。詳細は 5.4.15 節
   - `Gradient` → `{type, value}` ラッパー付きサマリー（`colorKeyCount`, `alphaKeyCount`, `mode`）。詳細は 5.4.16 節
   - Enum → `{type, value}` ラッパー付き文字列名
   - `UnityEngine.Object` 派生（Scene 参照）→ `{type, value, is_object_ref, ref_path}` ラッパー付き。null 参照は `null`（フラット）
   - `UnityEngine.Object` 派生（Asset 参照）→ `{type, value, is_asset_ref, asset_path}` ラッパー付き。null 参照は `null`（フラット）
   - 配列 / List → `{type, value}` ラッパー付き。要素を `max_array_elements` まで展開。制限については 4.3.1 節参照
   - `[Serializable]` 構造体/クラス → `{type, value}` ラッパー付き。子フィールドを再帰的に展開（子フィールドも同じプリミティブ/ラッパー規則に従う）。制限については 4.3.1 節参照

#### 4.3.1 展開制限
配列/List と `[Serializable]` のネストは以下の制限付きで展開する:

1. **配列/List 要素数上限**: `max_array_elements` パラメータで指定（default: 16, max: 64）。超過時は先頭 N 要素のみ返し、`"_truncated": true` と `"_total_count": N` を付与する。`max_array_elements=0` の場合は要素を展開せず `"_total_count"` のみ返す
2. **再帰深度上限**: 最大 3 階層（ルートフィールドを深度 0 とする）。上限に達した場合はそのノードを `"..."` で省略する
3. **総フィールド数上限**: 1 レスポンスあたり最大 512 フィールド。上限到達後の残りフィールドは省略し、レスポンスに `"_fields_truncated": true` を付与する

配列/List の出力形式:
```json
{
  "type": "UnityEngine.Transform[]",
  "value": [
    { "value": "Point1 (Transform)", "is_object_ref": true, "ref_path": "/SpawnPoints/Point1" },
    { "value": "Point2 (Transform)", "is_object_ref": true, "ref_path": "/SpawnPoints/Point2" },
    { "value": "Point3 (Transform)", "is_object_ref": true, "ref_path": "/SpawnPoints/Point3" }
  ]
}
```

切り詰め時（`max_array_elements` を超過した場合）:
```json
{
  "type": "UnityEngine.Transform[]",
  "value": [
    { "value": "Point1 (Transform)", "is_object_ref": true, "ref_path": "/Points/P1" },
    "... (残り省略)"
  ],
  "_truncated": true,
  "_total_count": 128
}
```

`max_array_elements=0` の場合（要素数のみ）:
```json
{
  "type": "UnityEngine.Transform[]",
  "_total_count": 64
}
```

`[Serializable]` 構造体/クラスの出力形式（子フィールドもプリミティブ/ラッパー規則に従う）:
```json
{
  "type": "MyGame.EnemyStats",
  "value": {
    "hp": 100,
    "attackPower": 15.0,
    "element": { "type": "MyGame.ElementType", "value": "Fire" }
  }
}
```

3. **Scene 参照と Asset 参照の判別**:
   - `EditorUtility.IsPersistent(obj)` が `true` → Asset 参照（`is_asset_ref: true`）
   - `false` → Scene 参照（`is_object_ref: true`）
4. **参照先パスの解決**:
   - Scene 参照の場合、参照先オブジェクトが `GameObject` なら hierarchy パスを算出する
   - 参照先が `Component` の場合、その `gameObject` の hierarchy パスを算出する
   - パス算出不可の場合は `ref_path` を省略する

### 4.4 動作ルール
1. `game_object_path` で Scene hierarchy を検索する（共通ロジック 9.1 節）。
2. 見つからない場合は `ERR_OBJECT_NOT_FOUND` を返す。
3. `index` で GameObject のコンポーネントリストから対象を取得する（`GetComponents<Component>()[index]`）。
4. `index` が範囲外の場合は `ERR_COMPONENT_INDEX_OUT_OF_RANGE` を返す。
5. 対象コンポーネントが null（Missing Script）の場合は `ERR_MISSING_SCRIPT` を返す。
6. `fields` が指定されている場合、指定フィールドのみをレスポンスに含める。存在しないフィールド名は無視する。
7. `max_array_elements` に従い、配列/List の展開要素数を制限する（4.3.1 節）。
8. 実処理は Unity メインスレッドで実行する。

### 4.5 メタデータ
1. `execution_mode`: `sync`
2. `supports_cancel`: `false`
3. `default_timeout_ms`: `10000`
4. `max_timeout_ms`: `30000`
5. `requires_client_request_id`: `false`
6. `execution_error_retryable`: `true`

### 4.6 エラー仕様
| エラーコード | 発生条件 | retryable |
|---|---|---|
| `ERR_OBJECT_NOT_FOUND` | `game_object_path` の GameObject が見つからない | false |
| `ERR_COMPONENT_INDEX_OUT_OF_RANGE` | `index` がコンポーネント数の範囲外 | false |
| `ERR_MISSING_SCRIPT` | `index` のコンポーネントが Missing Script（null） | false |
| `ERR_INVALID_PARAMS` | 必須パラメータ欠損、型不正 | false |

※ Server → MCP クライアントへのエラー伝搬は 5.6 節の二層構造に従う。

---

## 5. `manage_component` (edit)

### 5.1 入力仕様

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
      "description": "0-based component index on the GameObject (matches get_scene_hierarchy output). Required for 'update'/'remove'/'move' to identify the target component. Optional for 'add' to specify insertion position (must be >= 1 since index 0 is Transform; default: append to end)."
    },
    "new_index": {
      "type": "integer",
      "minimum": 0,
      "description": "Target position for 'move' action. Required for 'move' only."
    },
    "fields": {
      "type": "object",
      "description": "Key-value map of serialized field names to values. Values follow the Field Value Format described in section 5.2. Applicable to 'add' and 'update' actions.",
      "additionalProperties": true
    }
  },
  "required": ["action", "game_object_path"],
  "additionalProperties": false
}
```

#### action ごとの必須・任意パラメータ

| action | 必須 | 任意 | 説明 |
|---|---|---|---|
| `add` | `component_type` | `index`, `fields` | コンポーネントを追加する。`index` で挿入位置を指定（省略時は末尾） |
| `update` | `index`, `fields` | — | 既存コンポーネントのフィールドを更新する |
| `remove` | `index` | — | コンポーネントを削除する |
| `move` | `index`, `new_index` | — | コンポーネントの順序を変更する |

action ごとの必須パラメータが欠損している場合は `ERR_INVALID_PARAMS` を返す。

### 5.2 Field Value Format

`fields` の各値は以下のいずれかの形式をとる。`add` および `update` action で使用する。

#### 5.2.1 プリミティブ値
JSON のプリミティブをそのまま使用する。

```json
{
  "speed": 5.0,
  "playerName": "Hero",
  "isActive": true,
  "maxHp": 100
}
```

対応する C# 型: `int`, `float`, `double`, `bool`, `string`, `long`

#### 5.2.2 Unity 値型
JSON オブジェクトで各成分を指定する。フィールドの C# 型から自動判定する。

```json
{
  "direction": { "x": 1.0, "y": 0.0, "z": 0.0 },
  "gridPos": { "x": 3, "y": 7, "z": 0 },
  "color": { "r": 1.0, "g": 0.0, "b": 0.0, "a": 1.0 },
  "rotation": { "x": 0.0, "y": 90.0, "z": 0.0, "w": 1.0 },
  "area": { "x": 0, "y": 0, "width": 100, "height": 50 },
  "region": { "position": { "x": 0, "y": 0, "z": 0 }, "size": { "x": 1, "y": 1, "z": 1 } },
  "cullingMask": 256,
  "fadeCurve": {
    "keys": [
      { "time": 0.0, "value": 0.0, "inTangent": 0.0, "outTangent": 1.0 },
      { "time": 1.0, "value": 1.0, "inTangent": 1.0, "outTangent": 0.0 }
    ],
    "preWrapMode": "ClampForever",
    "postWrapMode": "ClampForever"
  },
  "colorGradient": {
    "colorKeys": [
      { "color": { "r": 1.0, "g": 0.0, "b": 0.0, "a": 1.0 }, "time": 0.0 },
      { "color": { "r": 0.0, "g": 0.0, "b": 1.0, "a": 1.0 }, "time": 1.0 }
    ],
    "alphaKeys": [
      { "alpha": 1.0, "time": 0.0 },
      { "alpha": 1.0, "time": 1.0 }
    ],
    "mode": "Blend"
  }
}
```

対応する C# 型:

| 型 | JSON 形式 |
|---|---|
| `Vector2` | `{x, y}` |
| `Vector3` | `{x, y, z}` |
| `Vector4` | `{x, y, z, w}` |
| `Vector2Int` | `{x, y}` (整数) |
| `Vector3Int` | `{x, y, z}` (整数) |
| `Quaternion` | `{x, y, z, w}` |
| `Color` | `{r, g, b, a}` (`a` 省略時 1.0) |
| `Rect` | `{x, y, width, height}` |
| `RectInt` | `{x, y, width, height}` (整数) |
| `Bounds` | `{center:{x,y,z}, size:{x,y,z}}` |
| `BoundsInt` | `{position:{x,y,z}, size:{x,y,z}}` (整数) |
| `LayerMask` | integer (ビットマスク) |
| `AnimationCurve` | `{keys:[{time,value,inTangent,outTangent,...}], preWrapMode, postWrapMode}` |
| `Gradient` | `{colorKeys:[{color,time}], alphaKeys:[{alpha,time}], mode}` |

#### 5.2.3 列挙型
文字列名または整数値で指定する。

```json
{
  "mode": "Chase",
  "priority": 2
}
```

#### 5.2.4 Scene Object 参照 (`$ref`)
Scene 上の GameObject または Component への参照。`$ref` キーの存在で判別する。

**GameObject 参照:**
```json
{
  "targetObject": {
    "$ref": "/Enemy"
  }
}
```

**Component 参照（明示指定）:**
```json
{
  "targetRigidbody": {
    "$ref": "/Player",
    "component": "Rigidbody"
  }
}
```

ルール:
1. `$ref` は Scene hierarchy path（対象 GameObject の場所）を指定する。
2. `component` は省略可能。省略時はフィールドの C# 型に基づいて解決する:
   - フィールド型が `GameObject` → 参照先 GameObject そのものを代入
   - フィールド型が `Component` 派生（`Transform`, `Rigidbody`, 任意の `MonoBehaviour` 等）→ 参照先 GameObject から `GetComponent` で取得して代入
3. `component` を明示した場合、指定した型のコンポーネントを `GetComponent` で取得して代入する。
4. 参照先が見つからない場合は `ERR_REFERENCE_NOT_FOUND` エラーを返す。

#### 5.2.5 Asset 参照 (`$asset`)
AssetDatabase 上のアセットへの参照。`$asset` キーの存在で判別する。

```json
{
  "material": {
    "$asset": "Assets/Materials/Red.mat"
  }
}
```

ルール:
1. `$asset` は AssetDatabase 上のパスを指定する。
2. `AssetDatabase.LoadAssetAtPath` で読み込み、フィールドに代入する。
3. アセットが見つからない場合は `ERR_REFERENCE_NOT_FOUND` エラーを返す。

#### 5.2.6 配列 / List
JSON 配列で指定する。各要素は 5.2.1〜5.2.7 のいずれかの形式をとる（再帰可能）。配列全体を置換する（差分更新ではない）。

```json
{
  "waypoints": [
    { "$ref": "/SpawnPoints/Point1", "component": "Transform" },
    { "$ref": "/SpawnPoints/Point2", "component": "Transform" }
  ],
  "scores": [100, 200, 300],
  "colors": [
    { "r": 1.0, "g": 0.0, "b": 0.0, "a": 1.0 },
    { "r": 0.0, "g": 1.0, "b": 0.0, "a": 1.0 }
  ]
}
```

#### 5.2.7 [Serializable] 構造体/クラス
JSON オブジェクトで子フィールドを指定する。キーは SerializedProperty のフィールド名に対応する。

```json
{
  "stats": {
    "hp": 100,
    "attackPower": 15.0,
    "element": "Fire"
  }
}
```

ネスト可能:
```json
{
  "config": {
    "movement": {
      "speed": 5.0,
      "jumpHeight": 2.0
    },
    "targets": [
      { "$ref": "/Enemy1" },
      { "$ref": "/Enemy2" }
    ]
  }
}
```

#### 5.2.8 null（参照クリア）
ObjectReference フィールドの参照を解除する場合は `null` を指定する。

```json
{
  "target": null
}
```

Plugin は `serializedObject.FindProperty(name).objectReferenceValue = null` で参照をクリアする。
`$ref` / `$asset` でもプリミティブでもない値として、5.4.6 節の分岐より先に判定する。

### 5.3 出力仕様

全 action 共通フィールド:
1. `action`: 実行した操作種別（入力と同値）
2. `game_object_path`: 対象 GameObject の Scene hierarchy パス
3. `game_object_name`: 対象 GameObject の名前
4. `component_type`: 対象コンポーネントの完全修飾型名（`Type.FullName`）
5. `index`: 操作後のコンポーネントの index

#### 5.3.1 `add` の出力

```json
{
  "action": "add",
  "game_object_path": "/Canvas/Panel",
  "game_object_name": "Panel",
  "component_type": "MyGame.PlayerController",
  "index": 3,
  "fields_set": ["speed", "targetObject", "material"],
  "fields_skipped": []
}
```

追加フィールド:
- `fields_set`: 設定に成功したフィールド名のリスト
- `fields_skipped`: スキップされたフィールド名のリスト（フィールドが存在しない等）

#### 5.3.2 `update` の出力

```json
{
  "action": "update",
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "component_type": "UnityEngine.Rigidbody",
  "index": 1,
  "fields_set": ["mass", "useGravity"],
  "fields_skipped": []
}
```

追加フィールド:
- `fields_set`: 設定に成功したフィールド名のリスト
- `fields_skipped`: スキップされたフィールド名のリスト

#### 5.3.3 `remove` の出力

```json
{
  "action": "remove",
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "component_type": "UnityEngine.AudioSource",
  "index": 4
}
```

追加フィールドなし。

#### 5.3.4 `move` の出力

```json
{
  "action": "move",
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "component_type": "UnityEngine.Rigidbody",
  "index": 3,
  "previous_index": 1
}
```

追加フィールド:
- `previous_index`: 移動前の index

### 5.4 動作ルール

#### 5.4.1 共通: Play Mode ガードと GameObject の解決
1. Play Mode 中の場合、`ERR_PLAY_MODE_ACTIVE` を返す。エラーメッセージに「`control_play_mode` で停止してから操作してください」を含める。
2. `game_object_path` で Scene hierarchy を検索する（共通ロジック 9.1 節）。
3. 見つからない場合は `ERR_OBJECT_NOT_FOUND` を返す。

#### 5.4.2 `add`: コンポーネントの追加
1. `component_type` で型を解決する（共通ロジック 9.2 節）。
2. 型が見つからない場合は `ERR_COMPONENT_TYPE_NOT_FOUND` を返す。
3. 型が `Component` を継承していない場合は `ERR_INVALID_COMPONENT_TYPE` を返す。
4. `index` が指定されている場合の事前検証:
   - `index == 0` なら `ERR_INVALID_PARAMS` を返す（Transform の前への挿入は不可）
   - `index > 追加前のコンポーネント数` なら `ERR_COMPONENT_INDEX_OUT_OF_RANGE`（末尾への挿入を許容するため `=` は許可）
5. `Undo.AddComponent` で末尾にコンポーネントを追加する。
6. `index` が指定されている場合、`ComponentUtility.MoveComponentUp` を繰り返して `index` の位置に移動する。
7. `index` が省略されている場合は末尾のまま（移動なし）。
8. `fields` が指定されている場合はフィールド設定を行う（5.4.6 節）。

#### 5.4.3 `update`: フィールドの更新
1. `index` で既存コンポーネントを取得する（`GetComponents<Component>()[index]`）。
2. `index >= コンポーネント数` の場合は `ERR_COMPONENT_INDEX_OUT_OF_RANGE` を返す。
3. `fields` に従ってフィールド設定を行う（5.4.6 節）。`fields` は必須。

#### 5.4.4 `remove`: コンポーネントの削除
1. `index` で既存コンポーネントを取得する。
2. `index >= コンポーネント数` の場合は `ERR_COMPONENT_INDEX_OUT_OF_RANGE` を返す。
3. `index == 0`（Transform）の場合は `ERR_INVALID_PARAMS` を返す（Transform は削除不可）。
4. 同一 GameObject 上の他コンポーネントが `[RequireComponent]` で対象コンポーネントに依存している場合は `ERR_COMPONENT_DEPENDENCY` を返す。依存元のコンポーネント型名をエラー詳細に含める。
5. `Undo.DestroyObjectImmediate(component)` でコンポーネントを削除する。

#### 5.4.5 `move`: コンポーネントの並べ替え
1. `index` で既存コンポーネントを取得する。
2. `index >= コンポーネント数` または `new_index >= コンポーネント数` の場合は `ERR_COMPONENT_INDEX_OUT_OF_RANGE` を返す。
3. `index == 0` または `new_index == 0` の場合は `ERR_INVALID_PARAMS` を返す（Transform の位置は変更不可）。
4. `index == new_index` の場合は何もせず現状を返す。
5. `ComponentUtility.MoveComponentUp` / `MoveComponentDown` を繰り返して `new_index` の位置に移動する。

#### 5.4.6 フィールドの設定（`add` / `update` 共通）

`get_component_info`（読み取り）と同じ `SerializedObject` / `SerializedProperty` API を使用する。リフレクション（`GetField` / `GetProperty`）は使わない。これにより読み取りで見えるフィールドと書き込み可能なフィールドが一致し、round-trip の整合性を保つ。

1. `new SerializedObject(component)` を生成する。
2. `fields` の各エントリについて、`serializedObject.FindProperty(name)` でプロパティを検索する。
3. プロパティが見つからない場合、そのフィールドを `fields_skipped` に追加し、処理を続行する。
4. 値の変換は以下の順で分岐する:
   - 値が `null`: `objectReferenceValue = null` で参照をクリア（5.2.8 節）
   - `$ref` あり: Scene object 参照解決（5.4.7 節）→ `SerializedProperty.objectReferenceValue` に設定
   - `$asset` あり: Asset 参照解決（5.4.8 節）→ `SerializedProperty.objectReferenceValue` に設定
   - それ以外: `SerializedProperty` の型に応じた値変換（5.4.9 節）
5. 全フィールド設定後に `serializedObject.ApplyModifiedProperties()` を呼ぶ（Undo 自動記録）。
6. Prefab インスタンスの場合は `PrefabUtility.RecordPrefabInstancePropertyModifications(component)` を呼ぶ。

#### 5.4.7 Scene Object 参照の解決
1. `$ref` で指定されたパスの GameObject を 共通ロジック 9.1 節と同じ方法で検索する。
2. `component` キーが指定されている場合:
   - 対象 GameObject から `GetComponent(componentType)` で取得する
   - 見つからない場合は `ERR_REFERENCE_NOT_FOUND`
3. `component` キーが省略されている場合:
   - フィールドの宣言型が `GameObject` → 対象 GameObject を代入
   - フィールドの宣言型が `Component` 派生 → `GetComponent(fieldType)` で取得して代入
   - 見つからない場合は `ERR_REFERENCE_NOT_FOUND`

#### 5.4.8 Asset 参照の解決
1. `$asset` のパスを `AssetDatabase.LoadAssetAtPath(path, fieldType)` で読み込む。
2. null の場合は `ERR_REFERENCE_NOT_FOUND` を返す。

#### 5.4.9 値の変換
`SerializedProperty.propertyType` に基づいて JSON 値を変換する。

| `SerializedPropertyType` | JSON 形式 | 設定方法 |
|---|---|---|
| `Integer` | number | `intValue` / `longValue` |
| `Float` | number | `floatValue` / `doubleValue` |
| `Boolean` | boolean | `boolValue` |
| `String` | string | `stringValue` |
| `Vector2` | `{x,y}` | `vector2Value` |
| `Vector3` | `{x,y,z}` | `vector3Value` |
| `Vector4` | `{x,y,z,w}` | `vector4Value` |
| `Vector2Int` | `{x,y}` (整数) | `vector2IntValue` |
| `Vector3Int` | `{x,y,z}` (整数) | `vector3IntValue` |
| `Quaternion` | `{x,y,z,w}` | `quaternionValue` |
| `Color` | `{r,g,b,a}` | `colorValue`（`a` 省略時 1.0） |
| `Rect` | `{x,y,width,height}` | `rectValue` |
| `RectInt` | `{x,y,width,height}` (整数) | `rectIntValue` |
| `Bounds` | `{center:{...},size:{...}}` | `boundsValue` |
| `BoundsInt` | `{position:{...},size:{...}}` (整数) | `boundsIntValue` |
| `LayerMask` | number (ビットマスク) | `intValue` |
| `AnimationCurve` | `{keys:[...],preWrapMode,postWrapMode}` | `animationCurveValue`（5.4.15 節） |
| `Gradient` | `{colorKeys:[...],alphaKeys:[...],mode}` | `gradientValue`（5.4.16 節） |
| `Enum` | string or number | `enumValueIndex` / `enumValueFlag` |
| `ObjectReference` | `$ref` / `$asset` | `objectReferenceValue`（5.4.7 / 5.4.8 節で解決） |
| `ArraySize` (配列/List) | JSON array | 5.4.13 節参照 |
| `Generic` ([Serializable]) | JSON object | 5.4.14 節参照 |

未対応の `propertyType` はスキップし、`fields_skipped` に追加する。

#### 5.4.10 Undo グループ
全 action の変更操作を 1 つの Undo グループにまとめる。ユーザーが Unity Editor 上で Ctrl+Z 1 回で操作全体を取り消せるようにする。`add` / `update` は 5.4.11 節の Phase 2 で、`remove` / `move` も同様に `Undo.CollapseUndoOperations` で囲む。

#### 5.4.11 事前検証による原子性（validate-then-apply）
`add` / `update` で `fields` を含むリクエストは、状態変更を一切行う前に全入力を検証する。

**Phase 1: 検証（状態変更なし）**
1. `game_object_path` → GameObject を解決する
2. `add` の場合: `component_type` → 型を解決する
3. `update` の場合: `index` → 既存コンポーネントを取得する
4. `fields` 内の全エントリを再帰的に走査し（配列要素・ネスト構造体の中も含む）、参照を事前解決する:
   - `$ref` → 参照先 GameObject / Component を検索し、解決済みオブジェクトを保持する
   - `$asset` → `AssetDatabase.LoadAssetAtPath` でアセットを読み込み、保持する
   - 1 件でも解決に失敗した場合 → `ERR_REFERENCE_NOT_FOUND` で即座に失敗する（何も変更されていない）

**Phase 2: 適用（検証済みの値のみ使用）**
5. `Undo.IncrementCurrentGroup()` + `Undo.SetCurrentGroupName("manage_component: {action}")` で Undo グループを開始する
6. `add` の場合: `Undo.AddComponent` でコンポーネントを追加する
7. `SerializedObject` / `SerializedProperty` で Phase 1 の解決済みオブジェクトを代入する
8. `ApplyModifiedProperties` で変更を確定する
9. `Undo.CollapseUndoOperations` で Undo グループを閉じる

Undo グループにまとめることで、ユーザーが Unity Editor 上で Ctrl+Z 1 回で操作全体を取り消せる。

事前検証により、参照解決エラー時は Phase 2 に到達せずロールバック不要で原子性を保証する。プログラム的なロールバック（`Undo.PerformUndo()`）には依存しない。

#### 5.4.12 実行スレッド
全処理は Unity メインスレッドで実行する（`MainThreadDispatcher.InvokeAsync`）。

#### 5.4.13 配列/List の設定
JSON 配列を受け取り、`SerializedProperty` の配列操作で設定する。

1. `serializedProperty.ClearArray()` で既存要素を全削除する。
2. JSON 配列の各要素について `serializedProperty.InsertArrayElementAtIndex(i)` で要素を追加する。
3. 追加した各要素に対して、5.4.9 節の型変換を再帰的に適用する。
4. 要素内に `$ref` / `$asset` がある場合は Phase 1 で事前解決済みのオブジェクトを使用する。

入力形式:
```json
{
  "waypoints": [
    { "$ref": "/SpawnPoints/Point1", "component": "Transform" },
    { "$ref": "/SpawnPoints/Point2", "component": "Transform" }
  ]
}
```

プリミティブ配列の場合:
```json
{
  "scores": [100, 200, 300]
}
```

#### 5.4.14 [Serializable] 構造体/クラスの設定
JSON オブジェクトを受け取り、子プロパティを再帰的に設定する。

1. `fields` のキーに対して `serializedProperty.FindPropertyRelative(name)` で子プロパティを検索する。
2. 見つからない場合はそのキーをスキップする。
3. 見つかった子プロパティに対して、5.4.9 節の型変換を再帰的に適用する（配列やさらにネストされた構造体も含む）。
4. 再帰深度の上限は読み取り時と同じく 3 階層とする。上限を超えた場合はスキップする。

入力形式:
```json
{
  "stats": {
    "hp": 100,
    "attackPower": 15.0,
    "element": "Fire"
  }
}
```

#### 5.4.15 AnimationCurve の変換

**読み取り（出力）— サマリーのみ**（キーフレームの tangent 値を LLM が精密操作する場面は稀なため）:
```json
{
  "type": "UnityEngine.AnimationCurve",
  "value": { "keyCount": 5, "timeRange": [0.0, 1.0] }
}
```

**書き込み（入力）— フル形式**:
- `animationCurveValue` に新しい `AnimationCurve` を構築して代入する
- `keys` 配列の各要素から `Keyframe` を生成する
- `inWeight`, `outWeight`, `weightedMode` は省略可能（省略時はデフォルト値）
- `preWrapMode`, `postWrapMode` は省略可能（省略時は `ClampForever`）

```json
{
  "fadeCurve": {
    "keys": [
      { "time": 0.0, "value": 0.0, "inTangent": 0.0, "outTangent": 1.0 },
      { "time": 1.0, "value": 1.0, "inTangent": 1.0, "outTangent": 0.0 }
    ],
    "preWrapMode": "ClampForever",
    "postWrapMode": "ClampForever"
  }
}
```

#### 5.4.16 Gradient の変換

**読み取り（出力）— サマリーのみ**:
```json
{
  "type": "UnityEngine.Gradient",
  "value": { "colorKeyCount": 3, "alphaKeyCount": 2, "mode": "Blend" }
}
```

**書き込み（入力）— フル形式**:
- `gradientValue` に新しい `Gradient` を構築して代入する
- `colorKeys` 配列から `GradientColorKey[]` を、`alphaKeys` 配列から `GradientAlphaKey[]` を生成する
- `mode` は省略可能（省略時は `Blend`）

```json
{
  "colorGradient": {
    "colorKeys": [
      { "color": { "r": 1.0, "g": 0.0, "b": 0.0, "a": 1.0 }, "time": 0.0 },
      { "color": { "r": 0.0, "g": 0.0, "b": 1.0, "a": 1.0 }, "time": 1.0 }
    ],
    "alphaKeys": [
      { "alpha": 1.0, "time": 0.0 },
      { "alpha": 1.0, "time": 1.0 }
    ],
    "mode": "Blend"
  }
}
```

### 5.5 メタデータ
1. `execution_mode`: `sync`
2. `supports_cancel`: `false`
3. `default_timeout_ms`: `10000`
4. `max_timeout_ms`: `30000`
5. `requires_client_request_id`: `false`
6. `execution_error_retryable`: `false`

### 5.6 エラー仕様

| エラーコード | 発生条件 | retryable |
|---|---|---|
| `ERR_OBJECT_NOT_FOUND` | `game_object_path` の GameObject が見つからない | false |
| `ERR_COMPONENT_TYPE_NOT_FOUND` | `component_type` に一致する型が見つからない（`add`） | false |
| `ERR_COMPONENT_TYPE_AMBIGUOUS` | `component_type` が複数の型に一致する（`add`）。完全修飾名で再指定が必要 | false |
| `ERR_INVALID_COMPONENT_TYPE` | 指定型が `Component` を継承していない（`add`） | false |
| `ERR_COMPONENT_INDEX_OUT_OF_RANGE` | `index` / `new_index` がコンポーネント数の範囲外 | false |
| `ERR_MISSING_SCRIPT` | `index` のコンポーネントが Missing Script（null）（`update` / `remove` / `move`） | false |
| `ERR_REFERENCE_NOT_FOUND` | `$ref` / `$asset` の参照先が見つからない（`add` / `update`） | false |
| `ERR_COMPONENT_DEPENDENCY` | `[RequireComponent]` により他コンポーネントが依存しているため削除不可（`remove`） | false |
| `ERR_PLAY_MODE_ACTIVE` | Play Mode 中に edit 操作を試行した。`control_play_mode` で停止してから再実行 | false |
| `ERR_INVALID_PARAMS` | 必須パラメータ欠損、型不正、Transform 削除/移動の試行 | false |

エラーの二層構造:
- **Plugin 層**: 上表の具体エラーコード（`ERR_OBJECT_NOT_FOUND` 等）を `PluginException` で送出する。
- **Server 層**: Plugin からのエラーを `ERR_UNITY_EXECUTION` でラップし、具体コードを `details.plugin_error_code` に、メッセージを `details.message` に含めて MCP クライアントへ返す。
- **実行前エラー**: `ERR_EDITOR_NOT_READY` 等は Server 層で直接返す（既存ルール維持）。

MCP クライアントはエラー判定に `details.plugin_error_code` を使用する。

---

## 6. 使用例

6.1〜6.3 は連続したワークフロー。6.4〜6.8 は独立した単発例（各例の前提状態をコメントで明記）。

### 初期 Scene 状態（全例で共通の出発点）

```
Player: [Transform(0), Rigidbody(1), CapsuleCollider(2)]
Enemy: [Transform(0), Rigidbody(1)]
SpawnPoints/Point1: [Transform(0)]
```

### 6.1 Scene 全体の把握

**Request:**
```json
{ "name": "get_scene_hierarchy", "arguments": {} }
```

**Response:**
```json
{
  "scene_name": "GameScene",
  "scene_path": "Assets/Scenes/GameScene.unity",
  "root_game_objects": [
    {
      "name": "Main Camera", "path": "/Main Camera", "active": true,
      "components": ["UnityEngine.Transform", "UnityEngine.Camera", "UnityEngine.AudioListener"],
      "children": []
    },
    {
      "name": "Player", "path": "/Player", "active": true,
      "components": ["UnityEngine.Transform", "UnityEngine.Rigidbody", "UnityEngine.CapsuleCollider"],
      "children": [
        { "name": "Model", "path": "/Player/Model", "active": true, "components": ["UnityEngine.Transform", "UnityEngine.MeshRenderer", "UnityEngine.MeshFilter"], "children": [] }
      ]
    },
    {
      "name": "Enemy", "path": "/Enemy", "active": true,
      "components": ["UnityEngine.Transform", "UnityEngine.Rigidbody"],
      "children": []
    },
    {
      "name": "SpawnPoints", "path": "/SpawnPoints", "active": true,
      "components": ["UnityEngine.Transform"],
      "children": [
        { "name": "Point1", "path": "/SpawnPoints/Point1", "active": true, "components": ["UnityEngine.Transform"], "children": [] }
      ]
    }
  ],
  "total_game_objects": 6,
  "truncated": false
}
```

### 6.2 コンポーネントの詳細確認

hierarchy から Player の UnityEngine.Rigidbody が index=1 であることが分かったので、詳細を取得:

**Request:**
```json
{ "name": "get_component_info", "arguments": { "game_object_path": "/Player", "index": 1 } }
```

**Response:**
```json
{
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "index": 1,
  "component_type": "UnityEngine.Rigidbody",
  "fields": {
    "mass": 1.0,
    "drag": 0.0,
    "angularDrag": 0.05,
    "useGravity": true,
    "isKinematic": false
  }
}
```

### 6.3 スクリプトのアタッチと SerializeField の設定（add）

**Request:**
```json
{
  "name": "manage_component",
  "arguments": {
    "action": "add",
    "game_object_path": "/Player",
    "component_type": "EnemyChaser",
    "fields": {
      "chaseSpeed": 5.0,
      "target": { "$ref": "/Enemy" },
      "spawnPoint": { "$ref": "/SpawnPoints/Point1", "component": "Transform" },
      "alertMaterial": { "$asset": "Assets/Materials/Alert.mat" }
    }
  }
}
```

**Response:**
```json
{
  "action": "add",
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "component_type": "MyGame.EnemyChaser",
  "index": 3,
  "fields_set": ["chaseSpeed", "target", "spawnPoint", "alertMaterial"],
  "fields_skipped": []
}
```

→ Player は `[Transform(0), Rigidbody(1), CapsuleCollider(2), EnemyChaser(3)]` になった。

### 6.4 既存コンポーネントのフィールド更新（update）

前提: 6.3 実行後の状態。Player: `[Transform(0), Rigidbody(1), CapsuleCollider(2), EnemyChaser(3)]`

**Request:**
```json
{
  "name": "manage_component",
  "arguments": {
    "action": "update",
    "game_object_path": "/Player",
    "index": 1,
    "fields": {
      "mass": 2.5,
      "useGravity": false
    }
  }
}
```

**Response:**
```json
{
  "action": "update",
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "component_type": "UnityEngine.Rigidbody",
  "index": 1,
  "fields_set": ["mass", "useGravity"],
  "fields_skipped": []
}
```

### 6.5 コンポーネントの削除（remove）

前提: 初期状態。Player: `[Transform(0), Rigidbody(1), CapsuleCollider(2)]`

**Request:**
```json
{
  "name": "manage_component",
  "arguments": {
    "action": "remove",
    "game_object_path": "/Player",
    "index": 2
  }
}
```

**Response:**
```json
{
  "action": "remove",
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "component_type": "UnityEngine.CapsuleCollider",
  "index": 2
}
```

→ Player は `[Transform(0), Rigidbody(1)]` になった。

### 6.6 コンポーネントの並べ替え（move）

前提: 6.3 実行後の状態。Player: `[Transform(0), Rigidbody(1), CapsuleCollider(2), EnemyChaser(3)]`

**Request:**
```json
{
  "name": "manage_component",
  "arguments": {
    "action": "move",
    "game_object_path": "/Player",
    "index": 1,
    "new_index": 3
  }
}
```

**Response:**
```json
{
  "action": "move",
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "component_type": "UnityEngine.Rigidbody",
  "index": 3,
  "previous_index": 1
}
```

→ Player は `[Transform(0), CapsuleCollider(1), EnemyChaser(2), Rigidbody(3)]` になった。

### 6.7 同型コンポーネントの追加（add + index）

前提: 初期状態 + AudioSource が index=3 にある。Player: `[Transform(0), Rigidbody(1), CapsuleCollider(2), AudioSource(3)]`

2 つ目の AudioSource を index=2 の位置に挿入:

**Request:**
```json
{
  "name": "manage_component",
  "arguments": {
    "action": "add",
    "game_object_path": "/Player",
    "component_type": "UnityEngine.AudioSource",
    "index": 2,
    "fields": {
      "volume": 0.5,
      "loop": true
    }
  }
}
```

**Response:**
```json
{
  "action": "add",
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "component_type": "UnityEngine.AudioSource",
  "index": 2,
  "fields_set": ["volume", "loop"],
  "fields_skipped": []
}
```

→ Player は `[Transform(0), Rigidbody(1), AudioSource(2), CapsuleCollider(3), AudioSource(4)]` になった。

### 6.8 配列と [Serializable] の設定・読み取り（update）

前提: 初期状態 + EnemyChaser が index=3 にある。Player: `[Transform(0), Rigidbody(1), CapsuleCollider(2), EnemyChaser(3)]`

EnemyChaser は以下の SerializeField を持つ:
```csharp
[SerializeField] private Transform[] waypoints;
[SerializeField] private EnemyStats stats; // [Serializable] struct
```

**Request (update):**
```json
{
  "name": "manage_component",
  "arguments": {
    "action": "update",
    "game_object_path": "/Player",
    "index": 3,
    "fields": {
      "waypoints": [
        { "$ref": "/Waypoints/WP1", "component": "Transform" },
        { "$ref": "/Waypoints/WP2", "component": "Transform" },
        { "$ref": "/Waypoints/WP3", "component": "Transform" }
      ],
      "stats": {
        "hp": 150,
        "attackPower": 20.0,
        "element": "Ice"
      }
    }
  }
}
```

**Response:**
```json
{
  "action": "update",
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "component_type": "MyGame.EnemyChaser",
  "index": 3,
  "fields_set": ["waypoints", "stats"],
  "fields_skipped": []
}
```

**確認 (get_component_info):**
```json
{ "name": "get_component_info", "arguments": { "game_object_path": "/Player", "index": 3 } }
```

**Response:**
```json
{
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "index": 3,
  "component_type": "MyGame.EnemyChaser",
  "fields": {
    "chaseSpeed": 5.0,
    "waypoints": {
      "type": "UnityEngine.Transform[]",
      "value": [
        { "value": "WP1 (Transform)", "is_object_ref": true, "ref_path": "/Waypoints/WP1" },
        { "value": "WP2 (Transform)", "is_object_ref": true, "ref_path": "/Waypoints/WP2" },
        { "value": "WP3 (Transform)", "is_object_ref": true, "ref_path": "/Waypoints/WP3" }
      ]
    },
    "stats": {
      "type": "MyGame.EnemyStats",
      "value": {
        "hp": 150,
        "attackPower": 20.0,
        "element": { "type": "MyGame.ElementType", "value": "Ice" }
      }
    }
  }
}
```

### 6.9 設定結果の確認

前提: 6.3 実行後の状態。Player: `[Transform(0), Rigidbody(1), CapsuleCollider(2), EnemyChaser(3)]`

**Request:**
```json
{ "name": "get_component_info", "arguments": { "game_object_path": "/Player", "index": 3 } }
```

**Response:**
```json
{
  "game_object_path": "/Player",
  "game_object_name": "Player",
  "index": 3,
  "component_type": "MyGame.EnemyChaser",
  "fields": {
    "chaseSpeed": 5.0,
    "target": { "type": "UnityEngine.GameObject", "value": "Enemy", "is_object_ref": true, "ref_path": "/Enemy" },
    "spawnPoint": { "type": "UnityEngine.Transform", "value": "Point1 (Transform)", "is_object_ref": true, "ref_path": "/SpawnPoints/Point1" },
    "alertMaterial": { "type": "UnityEngine.Material", "value": "Alert (Material)", "is_asset_ref": true, "asset_path": "Assets/Materials/Alert.mat" }
  }
}
```

---

## 7. メタデータ一覧

| ツール名 | execution_mode | supports_cancel | default_timeout_ms | max_timeout_ms | requires_client_request_id | execution_error_retryable |
|---|---|---|---:|---:|---|---|
| `get_scene_hierarchy` | sync | false | 10000 | 30000 | false | true |
| `get_component_info` | sync | false | 10000 | 30000 | false | true |
| `manage_component` | sync | false | 10000 | 30000 | false | false |

分類根拠:
- 全ツールとも 5 秒以内完了見込み → `sync`
- 読み取り系（`get_scene_hierarchy`, `get_component_info`）→ `execution_error_retryable=true`
- 書き込み系（`manage_component`）→ `execution_error_retryable=false`（Undo 対応によりリカバリ可能）

---

## 8. 設計意図

### 8.1 3 ツール構成
1. 既存ツールと同じ細粒度（1 ツール = 1 目的）方針を踏襲する
2. 読み取りと書き込みを分離する。LLM は「まず読んで理解してから書く」ワークフローを取る
3. hierarchy（軽量な構造一覧）と個別詳細（フィールド値）を分離し、不要なデータ転送を避ける
4. 書き込み操作（追加・更新・削除・並べ替え）は `manage_component` に `action` パラメータで集約し、LLM に利用可能な操作手段を明示する

### 8.2 コンポーネントの `index` 識別
Unity では同一型のコンポーネントを 1 つの GameObject に複数アタッチできる（例: `AudioSource` x2）。型名だけでは一意に特定できないため、コンポーネント配列の `index` で識別する。

### 8.3 `$ref` / `$asset` 記法
`Vector3` 等の値型も JSON オブジェクトで表現するため、通常の値と参照を区別する必要がある。`$ref` / `$asset` という特殊キーの有無で判別する。

### 8.4 読み取り出力に参照情報を含める
`get_component_info` の出力に `is_object_ref` / `ref_path` / `is_asset_ref` / `asset_path` を含めることで、LLM が `manage_component` の `$ref` / `$asset` 入力を正しく構築できる。

### 8.5 事前検証による原子性
`manage_component` の `add` / `update` では、フィールド設定前に全参照を事前検証する（validate-then-apply）。部分成功を防ぎ、エラー時は全体失敗とする。

### 8.6 Transform の保護
Transform は Unity の必須コンポーネントであり、常に index=0 に位置する。`remove` で index=0 の削除を禁止し、`move` で index=0 の移動を禁止する。

---

## 9. 共通ロジック

### 9.1 GameObject の検索（全 3 ツールで共通）
1. `GameObject.Find(path)` を最初に試行する。
2. 見つからない場合は hierarchy path をパーツ分解し、アクティブ Scene のルート GameObject から `Transform.Find` で順次探索する。
3. Unity Plugin 側で共通ヘルパーメソッドとして実装する。

### 9.2 コンポーネント型の解決（`manage_component` の `add` で使用）
1. `Type.GetType(typeName)` で直接検索（完全修飾名）
2. Unity 標準名前空間プレフィックスで検索: `UnityEngine`, `UnityEngine.UI`, `UnityEngine.EventSystems`, `UnityEngine.Animations`, `UnityEngine.Rendering`, `TMPro`
3. `AppDomain.CurrentDomain.GetAssemblies()` 全体から名前一致で検索
4. ステップ 2-3 で複数の型が一致した場合は `ERR_COMPONENT_TYPE_AMBIGUOUS` を返す。エラー詳細に一致した完全修飾名の一覧を含める
5. Unity Plugin 側で共通ヘルパーメソッドとして実装する。

---

## 10. 将来の拡張候補（v1 では非対応）
1. 複数コンポーネントの一括操作（batch）
2. 複数 Scene のサポート（Additive Scene）
3. Prefab 編集モードのサポート
