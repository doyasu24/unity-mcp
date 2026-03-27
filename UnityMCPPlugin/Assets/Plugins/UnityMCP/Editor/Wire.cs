using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;

namespace UnityMcpPlugin
{
    internal static class Wire
    {
        internal const int ProtocolVersion = 1;

        /// <summary>
        /// package.json の version を読み取る。ハードコードではなく動的取得することで
        /// リリース時の更新漏れを防ぐ。
        /// </summary>
        internal static readonly string PluginVersion = ReadPluginVersion();

        internal const int EditorStatusIntervalMs = 5000;

        /// <summary>
        /// [CallerFilePath] でこのファイル (Editor/Wire.cs) のコンパイル時パスを取得し、
        /// 親ディレクトリを辿って package.json の version を読み取る。
        /// UPM インストール時もローカル開発時もディレクトリ構造は同一なので動作する。
        /// </summary>
        private static string ReadPluginVersion([CallerFilePath] string callerPath = "")
        {
            try
            {
                var editorDir = Path.GetDirectoryName(callerPath);
                var packageDir = Path.GetDirectoryName(editorDir);
                var packageJsonPath = Path.Combine(packageDir, "package.json");
                if (!File.Exists(packageJsonPath)) return "0.0.0";

                var json = JObject.Parse(File.ReadAllText(packageJsonPath));
                return json["version"]?.ToString() ?? "0.0.0";
            }
            catch
            {
                // バージョン取得失敗は致命的ではないため、TypeInitializationException を防ぐ
                return "0.0.0";
            }
        }

        internal static string ToWireState(EditorBridgeState state)
        {
            switch (state)
            {
                case EditorBridgeState.Ready:
                    return "ready";
                case EditorBridgeState.Compiling:
                    return "compiling";
                case EditorBridgeState.Reloading:
                    return "reloading";
                case EditorBridgeState.EnteringPlayMode:
                    return "entering_play_mode";
                case EditorBridgeState.ExitingPlayMode:
                    return "exiting_play_mode";
                case EditorBridgeState.RunningTests:
                    return "running_tests";
                default:
                    return "ready";
            }
        }

    }
}
