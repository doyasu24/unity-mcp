# Asset Tools 仕様 v1

- Status: Draft
- Date: 2026-03-02
- Target: `Server` / `UnityMCPPlugin`

## 1. 目的

MCP からアセットの検索・詳細情報取得・作成・削除、およびマテリアルのプロパティ操作を行えるようにする。

LLM のワークフロー:
1. `find_assets` でプロジェクト内のアセットを検索
2. `get_asset_info` でアセットの詳細メタデータを取得
3. `manage_asset` でアセットを作成・削除、マテリアルのシェーダープロパティ操作・シェーダー変更・キーワード制御

## 2. ツール一覧

| # | ツール名 | 種別 | timeout(ms) |
|---|---|---|---|
| 1 | `find_assets` | read-only | 10000/30000 |
| 2 | `get_asset_info` | read-only | 10000/30000 |
| 3 | `manage_asset` | edit | 15000/30000 |

timeout 列は `default_timeout_ms / max_timeout_ms`。

---

## 3. `find_assets` (read-only)

### 3.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "filter": {
      "type": "string",
      "description": "AssetDatabase search filter (e.g. \"t:Material\", \"t:Prefab player\", \"l:MyLabel\")."
    },
    "search_in_folders": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Optional list of folder paths to limit the search scope (e.g. [\"Assets/Prefabs\"])."
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
  "required": ["filter"],
  "additionalProperties": false
}
```

### 3.2 出力仕様

```json
{
  "assets": [
    {
      "path": "Assets/Materials/Default.mat",
      "type": "Material",
      "name": "Default.mat",
      "guid": "abc123def456"
    }
  ],
  "count": 1,
  "truncated": false,
  "total_count": 1
}
```

追加フィールド:
- `total_count` (int): フィルタに一致するアセット総数（全ページ合計）
- `next_offset` (int, optional): `truncated=true` 時のみ。次ページ取得時に `offset` に渡す値

### 3.3 動作ルール

1. `search_in_folders` 指定時は `AssetDatabase.FindAssets(filter, searchInFolders)` で GUID を取得。未指定時は `AssetDatabase.FindAssets(filter)`。
2. `AssetDatabase.GUIDToAssetPath` でパス、`GetMainAssetTypeAtPath` で型名を取得。
3. `type` は `Type.Name`（例: `Material`, `GameObject`, `SceneAsset`）。
4. `name` は `Path.GetFileName(assetPath)` で取得したファイル名。
5. `guid` は `AssetDatabase.FindAssets` が返す GUID 文字列。
6. `offset` の位置から `max_results` 件を返す。残りがある場合は `truncated=true` と `next_offset` を設定。

### 3.4 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |


---

## 4. `get_asset_info` (read-only)

### 4.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "asset_path": {
      "type": "string",
      "description": "Asset path (e.g. \"Assets/Materials/Default.mat\")."
    }
  },
  "required": ["asset_path"],
  "additionalProperties": false
}
```

### 4.2 出力仕様

```json
{
  "asset_path": "Assets/Materials/Default.mat",
  "name": "Default",
  "type": "Material",
  "guid": "abc123def456",
  "file_size": 1234,
  "properties": {
    "shader": "Standard",
    "render_queue": 2000
  }
}
```

フィールド定義:
1. `asset_path`: アセットパス（入力と同値）
2. `name`: アセット名（`asset.name`）
3. `type`: アセット型名（`Type.Name`）
4. `guid`: GUID
5. `file_size`: ファイルサイズ（バイト）
6. `properties`: 型別のプロパティ情報

### 4.3 型別プロパティ

| アセット型 | properties の内容 |
|---|---|
| `Material` | `shader` (string), `render_queue` (int) |
| `Texture2D` | `width` (int), `height` (int), `format` (string), `mip_count` (int) |
| `AudioClip` | `length_seconds` (float), `channels` (int), `frequency` (int) |
| `Mesh` | `vertex_count` (int), `sub_mesh_count` (int), `bounds` (object: center + size) |
| `AnimationClip` | `length_seconds` (float), `frame_rate` (float) |
| `ScriptableObject` | シリアライズされたフィールド（上限あり） |
| その他 | 空オブジェクト `{}` |

### 4.4 動作ルール

1. `AssetDatabase.LoadMainAssetAtPath(asset_path)` でロード。null なら `ERR_OBJECT_NOT_FOUND`。
2. `AssetDatabase.AssetPathToGUID` で GUID を取得。
3. ファイルサイズは `FileInfo.Length` で取得。
4. 型に応じた `BuildProperties` メソッドでプロパティ情報を構築。
5. `ScriptableObject` のフィールドは `SerializedObject` で列挙し、上限数（`MaxFieldCount`）まで返す。

### 4.5 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `10000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |


---

## 5. `manage_asset` (edit)

### 5.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "action": {
      "type": "string",
      "enum": ["create", "delete", "get_properties", "set_properties", "set_shader", "get_keywords", "set_keywords"],
      "description": "Operation to perform. 'create': requires asset_type. 'delete': removes the asset. 'get_properties'/'set_properties'/'set_shader'/'get_keywords'/'set_keywords': material operations."
    },
    "asset_path": {
      "type": "string",
      "description": "Asset path (e.g. \"Assets/Materials/NewMat.mat\")."
    },
    "asset_type": {
      "type": "string",
      "enum": ["material", "folder", "physic_material", "animator_controller", "render_texture"],
      "description": "Type of asset to create. Required for 'create' action."
    },
    "properties": {
      "type": "object",
      "additionalProperties": true,
      "description": "Type-specific settings for 'create' (Material: { shader_name }, RenderTexture: { width, height, depth }). Property name-value map for 'set_properties'."
    },
    "overwrite": {
      "type": "boolean",
      "default": false,
      "description": "If true, overwrites an existing asset. Only applies to 'create' action."
    },
    "shader_name": {
      "type": "string",
      "description": "Shader name for 'set_shader' action."
    },
    "keywords": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Shader keywords for 'set_keywords'."
    },
    "keywords_action": {
      "type": "string",
      "enum": ["enable", "disable"],
      "description": "Whether to enable or disable keywords."
    }
  },
  "required": ["action", "asset_path"],
  "additionalProperties": false
}
```

### 5.2 アクション別パラメータ要件

| action | 必須追加パラメータ |
|---|---|
| `create` | `asset_type` |
| `delete` | なし |
| `get_properties` | なし |
| `set_properties` | `properties`（1 つ以上のエントリ） |
| `set_shader` | `shader_name` |
| `get_keywords` | なし |
| `set_keywords` | `keywords`（1 つ以上）, `keywords_action` |

### 5.3 アクション別出力仕様

#### `create`

```json
{
  "action": "create",
  "asset_path": "Assets/Materials/NewMat.mat",
  "asset_type": "material",
  "success": true
}
```

#### `delete`

```json
{
  "action": "delete",
  "asset_path": "Assets/Materials/NewMat.mat",
  "asset_type": "unknown",
  "success": true
}
```

#### `get_properties`

```json
{
  "asset_path": "Assets/Materials/MyMat.mat",
  "shader": "Standard",
  "render_queue": 2000,
  "properties": [
    { "name": "_Color", "type": "Color", "value": { "r": 1.0, "g": 0.5, "b": 0.0, "a": 1.0 } },
    { "name": "_MainTex", "type": "Texture", "value": { "$asset": "Assets/Textures/Diffuse.png", "type": "Texture2D" } },
    { "name": "_Metallic", "type": "Float", "value": 0.5 },
    { "name": "_GlossMapScale", "type": "Range", "value": 0.8, "range_min": 0.0, "range_max": 1.0 },
    { "name": "_Mode", "type": "Int", "value": 0 }
  ],
  "keyword_count": 2
}
```

プロパティ型と出力フォーマット:

| `ShaderPropertyType` | `type` 文字列 | `value` フォーマット |
|---|---|---|
| `Color` | `"Color"` | `{ "r": float, "g": float, "b": float, "a": float }` |
| `Vector` | `"Vector"` | `{ "x": float, "y": float, "z": float, "w": float }` |
| `Float` | `"Float"` | `float` |
| `Range` | `"Range"` | `float`（追加: `range_min`, `range_max`） |
| `Texture` | `"Texture"` | `{ "$asset": "Assets/...", "type": "Texture2D" }` or `null` |
| `Int` | `"Int"` | `int` |

#### `set_properties`

```json
{
  "asset_path": "Assets/Materials/MyMat.mat",
  "properties_set": ["_Color", "_Metallic"],
  "properties_skipped": ["_NonExistentProp"]
}
```

入力 `properties` のフォーマット:

| プロパティ型 | 入力値フォーマット | 例 |
|---|---|---|
| Color | `{ "r": float, "g": float, "b": float, "a": float }` | `{ "r": 1, "g": 0, "b": 0, "a": 1 }` |
| Float / Range | `float` | `0.8` |
| Int | `int` | `2` |
| Vector | `{ "x": float, "y": float, "z": float, "w": float }` | `{ "x": 0, "y": 1, "z": 0, "w": 0 }` |
| Texture | `{ "$asset": "Assets/..." }` or `null` | `{ "$asset": "Assets/Textures/Albedo.png" }` |

動作ルール:
1. `material.HasProperty(name)` で存在チェック。なければ `properties_skipped` に追加。
2. 型に応じた `Material.SetColor()` / `SetFloat()` / `SetInteger()` / `SetVector()` / `SetTexture()` を呼ぶ。
3. テクスチャ設定時、`$asset` のパスに対し `AssetDatabase.LoadAssetAtPath<Texture>()` でロード。見つからない場合は `properties_skipped` に追加。
4. 型と入力値のフォーマットが合わない場合は `properties_skipped` に追加。
5. `EditorUtility.SetDirty(material)` + `AssetDatabase.SaveAssets()` で保存。

#### `set_shader`

```json
{
  "asset_path": "Assets/Materials/MyMat.mat",
  "previous_shader": "Standard",
  "new_shader": "Universal Render Pipeline/Lit"
}
```

動作ルール:
1. `Shader.Find(shaderName)` でシェーダーを検索。見つからない場合は `ERR_SHADER_NOT_FOUND`。
2. `material.shader = newShader` でシェーダーを変更。
3. `EditorUtility.SetDirty(material)` + `AssetDatabase.SaveAssets()` で保存。

#### `get_keywords`

```json
{
  "asset_path": "Assets/Materials/MyMat.mat",
  "keywords": ["_NORMALMAP", "_EMISSION"]
}
```

動作ルール:
1. `material.shaderKeywords` から有効なキーワード一覧を取得。

#### `set_keywords`

```json
{
  "asset_path": "Assets/Materials/MyMat.mat",
  "keywords_action": "enable",
  "keywords_changed": ["_EMISSION"]
}
```

動作ルール:
1. `keywords_action == "enable"`: `material.IsKeywordEnabled(kw)` が `false` の場合のみ `EnableKeyword(kw)` を呼び、`keywords_changed` に追加。
2. `keywords_action == "disable"`: `material.IsKeywordEnabled(kw)` が `true` の場合のみ `DisableKeyword(kw)` を呼び、`keywords_changed` に追加。
3. 既に目的の状態にあるキーワードは `keywords_changed` に含まない（冪等操作）。
4. `EditorUtility.SetDirty(material)` + `AssetDatabase.SaveAssets()` で保存。

### 5.4 動作ルール

#### 共通
1. **Play Mode ガード**: Play Mode 中は `ERR_PLAY_MODE_ACTIVE` を返す。

#### `create`
1. `asset_type` 必須。サポート: `material`, `folder`, `physic_material`, `animator_controller`, `render_texture`。
2. 親ディレクトリの存在チェック（`folder` 以外）。存在しない場合は `ERR_INVALID_PARAMS`。
3. `overwrite=false`（デフォルト）でアセットが既に存在する場合は `ERR_ASSET_EXISTS`。
4. 型ごとの作成処理:
   - `material`: `Shader.Find(shader_name)` でシェーダーを取得し `new Material(shader)` で作成。`properties.shader_name` 省略時は `Standard`。シェーダーが見つからない場合は `ERR_INVALID_PARAMS`。
   - `folder`: `AssetDatabase.CreateFolder(parent, name)` で作成。
   - `physic_material`: `new PhysicsMaterial()` で作成。
   - `animator_controller`: `AnimatorController.CreateAnimatorControllerAtPath(path)` で作成。
   - `render_texture`: `new RenderTexture(width, height, depth)` で作成。`properties.width/height/depth` で設定可能（デフォルト: 256x256, depth=24）。
5. `AssetDatabase.SaveAssets()` で保存。

#### `delete`
1. `AssetDatabase.AssetPathToGUID(path)` で存在チェック。空なら `ERR_OBJECT_NOT_FOUND`。
2. `AssetDatabase.DeleteAsset(path)` で削除。失敗時は `ERR_UNITY_EXECUTION`。

### 5.5 メタデータ

| 項目 | 値 |
|---|---|
| `default_timeout_ms` | `15000` |
| `max_timeout_ms` | `30000` |
| `requires_client_request_id` | `false` |


---

## 6. エラー仕様

| エラーコード | 発生条件 | 対象ツール |
|---|---|---|
| `ERR_INVALID_PARAMS` | 必須パラメータ欠落、enum 値不正、親ディレクトリ不存在、`properties` が空 | 全ツール |
| `ERR_OBJECT_NOT_FOUND` | アセットが見つからない | `get_asset_info`, `manage_asset` (delete) |
| `ERR_PLAY_MODE_ACTIVE` | Play Mode 中の変更禁止 | `manage_asset` |
| `ERR_ASSET_EXISTS` | `overwrite=false` でアセットが既に存在 | `manage_asset` (create) |
| `ERR_ASSET_NOT_FOUND` | マテリアルアセットが見つからない | `manage_asset` (get_properties, set_properties, set_shader, get_keywords, set_keywords) |
| `ERR_NOT_A_MATERIAL` | アセットが Material でない | `manage_asset` (get_properties, set_properties, set_shader, get_keywords, set_keywords) |
| `ERR_SHADER_NOT_FOUND` | シェーダーが見つからない | `manage_asset` (set_shader) |
| `ERR_UNITY_EXECUTION` | アセット削除失敗など実行時エラー | `manage_asset` |

`set_properties` でシェーダーに存在しないプロパティを指定した場合はエラーにせず、`properties_skipped` に含めて返す。

---

## 7. 実装参照ファイル

### Server

| ファイル | 内容 |
|---|---|
| `Server/ToolCatalog.cs` | 全 3 ツールのスキーマ・メタデータ定義 |
| `Server/ToolContracts.cs` | `ToolNames`, `ManageAssetActions`, `AssetTypes`, `KeywordsActions`, Request/Result records |

### Unity Plugin

| ファイル | 内容 |
|---|---|
| `UnityMCPPlugin/.../CommandExecutor.cs` | `find_assets` の実行ロジック |
| `UnityMCPPlugin/.../Tools/GetAssetInfoTool.cs` | get_asset_info 実装（型別プロパティ構築） |
| `UnityMCPPlugin/.../Tools/ManageAssetTool.cs` | manage_asset 実装（create/delete、material アクションへのルーティング） |
| `UnityMCPPlugin/.../Tools/ManageMaterialTool.cs` | manage_asset の material アクション実装（get_properties/set_properties/set_shader/get_keywords/set_keywords） |
