# GameObject Management Tools 仕様 v1

- Status: Draft
- Date: 2026-03-01
- Target: `Server` / `UnityMCPPlugin`

## 1. 目的
MCP から Scene / Prefab 内の **GameObject 自体** を作成・編集・削除・親子関係変更できるようにする。既存の `manage_scene_component` / `manage_prefab_component` がコンポーネントレベルの CRUD を提供するのに対し、本ツールは GameObject レベルの操作を担う。

LLM のワークフロー:
1. `get_scene_hierarchy` / `get_prefab_hierarchy` で構造を把握
2. `manage_scene_game_object` / `manage_prefab_game_object` で GO の作成・更新・削除・reparent
3. `manage_scene_component` / `manage_prefab_component` でコンポーネントの追加・設定

## 2. 追加する tool

| # | ツール名 | 種別 | execution_mode | 役割 |
|---|---|---|---|---|
| 1 | `manage_scene_game_object` | edit | sync | Scene の GO 作成・更新・削除・reparent |
| 2 | `manage_prefab_game_object` | edit | sync | Prefab の GO 作成・更新・削除・reparent |

---

## 3. `manage_scene_game_object` (edit)

### 3.1 入力仕様

```json
{
  "type": "object",
  "properties": {
    "action": {
      "type": "string",
      "enum": ["create", "update", "delete", "reparent"],
      "description": "Operation to perform."
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

### 3.2 Action 別バリデーション

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

### 3.3 出力仕様

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

---

## 4. `manage_prefab_game_object` (edit)

`manage_scene_game_object` と同一スキーマに `prefab_path` (required) を追加。`game_object_path` と `parent_path` は Prefab ルートからの相対パス。

```
required: ["prefab_path", "action"]
```

出力にも `prefab_path` フィールドを含む。Prefab 版では reparent で `parent_path` を省略した場合、Prefab ルートが新しい親となる。Prefab ルート自体の delete/reparent はエラーとなる。

---

## 5. エラーコード

既存エラーに加え:

| コード | 説明 |
|---|---|
| `ERR_OBJECT_NOT_FOUND` | target GO / parent GO が見つからない |
| `ERR_PLAY_MODE_ACTIVE` | Play Mode 中の変更禁止 (Scene版のみ) |
| `ERR_INVALID_PARAMS` | パラメータ不正 |
| `ERR_CIRCULAR_HIERARCHY` | 自分自身への reparent、または先祖への reparent 検出 |
| `ERR_INVALID_TAG` | 未定義タグの指定 |
| `ERR_PREFAB_NOT_FOUND` | Prefab が見つからない (Prefab版のみ) |
| `ERR_PREFAB_SAVE_FAILED` | Prefab 保存失敗 (Prefab版のみ) |

---

## 6. 実装ファイル

### Server (.NET)
- `Server/ToolContracts.cs` — `ToolNames`, `GameObjectActions`, `PrimitiveTypes`, Request/Result records
- `Server/ToolCatalog.cs` — `ToolMetadata` entries with JSON schemas
- `Server/Mcp.cs` — Parse methods and switch arms
- `Server/UnityBridge.cs` — `ManageSceneGameObjectAsync`, `ManagePrefabGameObjectAsync`

### Plugin (Unity C#)
- `Editor/ToolContracts.cs` — `ToolNames` constants
- `Editor/Tools/SceneToolContracts.cs` — `CircularHierarchy`, `InvalidTag` error codes
- `Editor/Tools/ManageGameObjectTool.cs` — Scene 版 GO 管理
- `Editor/Tools/ManagePrefabGameObjectTool.cs` — Prefab 版 GO 管理
- `Editor/CommandExecutor.cs` — routing

## 7. Scene 版の Undo サポート

Scene 版では全アクションが Unity Undo システムに統合される:
- `create`: `Undo.RegisterCreatedObjectUndo` + `Undo.SetTransformParent`
- `update`: `Undo.RecordObject`
- `delete`: `Undo.DestroyObjectImmediate`
- `reparent`: `Undo.SetTransformParent`

Prefab 版では Undo 不要（LoadPrefabContents/SaveAsPrefabAsset パターン）。
