using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// 現在のエディタ選択状態を取得するツール。
    /// </summary>
    internal sealed class GetSelectionTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.GetSelection;

        public override object Execute(JObject parameters)
        {
            var activeObj = Selection.activeObject;
            SelectedObjectInfo activeInfo = null;
            if (activeObj != null)
            {
                activeInfo = BuildSelectedObjectInfo(activeObj);
            }

            var selectedObjects = Selection.objects;
            var selectedInfos = new List<SelectedObjectInfo>(selectedObjects.Length);
            foreach (var obj in selectedObjects)
            {
                if (obj != null)
                {
                    selectedInfos.Add(BuildSelectedObjectInfo(obj));
                }
            }

            return new GetSelectionPayload(activeInfo, selectedInfos, selectedInfos.Count);
        }

        private static SelectedObjectInfo BuildSelectedObjectInfo(Object obj)
        {
            var go = obj as GameObject;
            string path;
            if (go != null)
            {
                path = GameObjectResolver.GetHierarchyPath(go);
            }
            else
            {
                path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path))
                {
                    path = "";
                }
            }

            return new SelectedObjectInfo(obj.name, obj.GetInstanceID(), obj.GetType().Name, path);
        }
    }
}
