using System;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// LogBuffer と Unity Console をクリアするツール。
    /// LogBuffer.Clear はスレッドフリー、Unity Console クリアはメインスレッド必須のため
    /// AsyncToolHandler を使い自前でスレッド管理する。
    /// </summary>
    internal sealed class ClearConsoleTool : AsyncToolHandler
    {
        public override string ToolName => ToolNames.ClearConsole;

        public override async Task<object> ExecuteAsync(JObject parameters)
        {
            var clearedCount = LogBuffer.Clear();
            await MainThreadDispatcher.InvokeAsync(() =>
            {
                if (!TryClearUnityConsole())
                {
                    throw new PluginException("ERR_UNITY_EXECUTION", "failed to clear Unity Console");
                }

                return true;
            });

            return new ClearConsolePayload(true, clearedCount);
        }

        private static bool TryClearUnityConsole()
        {
            return TryInvokeClear("UnityEditor.LogEntries, UnityEditor") ||
                   TryInvokeClear("UnityEditorInternal.LogEntries, UnityEditor");
        }

        private static bool TryInvokeClear(string typeName)
        {
            var type = Type.GetType(typeName);
            if (type == null)
            {
                return false;
            }

            var clearMethod = type.GetMethod("Clear", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (clearMethod == null)
            {
                return false;
            }

            clearMethod.Invoke(null, null);
            return true;
        }
    }
}
