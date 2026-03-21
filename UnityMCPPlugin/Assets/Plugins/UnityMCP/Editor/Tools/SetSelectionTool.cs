using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// エディタの選択状態を設定するツール。
    /// パスまたは InstanceID でオブジェクトを指定する。
    /// </summary>
    internal sealed class SetSelectionTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.SetSelection;

        public override object Execute(JObject parameters)
        {
            var objects = new List<UnityEngine.Object>();

            if (parameters["paths"] is JArray pathsArray)
            {
                foreach (var token in pathsArray)
                {
                    var p = token?.Value<string>();
                    if (string.IsNullOrEmpty(p))
                    {
                        continue;
                    }

                    var go = GameObjectResolver.Resolve(p);
                    if (go != null)
                    {
                        objects.Add(go);
                        continue;
                    }

                    var asset = AssetDatabase.LoadMainAssetAtPath(p);
                    if (asset != null)
                    {
                        objects.Add(asset);
                    }
                }
            }

            if (parameters["instance_ids"] is JArray idsArray)
            {
                foreach (var token in idsArray)
                {
                    var id = token?.Value<int>() ?? 0;
                    if (id == 0)
                    {
                        continue;
                    }

                    #pragma warning disable CS0618
                    var obj = EditorUtility.InstanceIDToObject(id);
                    #pragma warning restore CS0618
                    if (obj != null)
                    {
                        objects.Add(obj);
                    }
                }
            }

            Selection.objects = objects.ToArray();
            return new SetSelectionPayload(true, objects.Count);
        }
    }
}
