using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityMcpPlugin
{
    [InitializeOnLoad]
    internal static class PluginBootstrap
    {
        static PluginBootstrap()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            var runtime = PluginRuntime.Instance;
            runtime.Initialize();

            CompilationPipeline.compilationStarted += _ => runtime.PublishEditorState(EditorBridgeState.Compiling);
            // compilationFinished は beforeAssemblyReload の前に発火する。
            // ここで即座に Ready を publish すると Server の EnsureEditorReadyAsync が
            // ドメインリロード前に return してしまう。
            // delayCall で次フレームまで遅延:
            //  - ドメインリロードが続く場合: delayCall はリロードで破棄。
            //    リロード後の [InitializeOnLoad] コンストラクタが Ready を publish。
            //  - リロード不要の場合 (コンパイルエラー等): 次フレームで Ready を publish。
            // compilationFinished は各アセンブリのコンパイル完了ごとに発火する。
            // マルチアセンブリの場合、中間のアセンブリ完了時に isCompiling はまだ true。
            // delayCall は1回限りのため、isCompiling=true だと Ready が永遠に publish されない。
            // EditorApplication.update で isCompiling=false になるまで繰り返しチェックする。
            CompilationPipeline.compilationFinished += _ =>
            {
                void CheckCompilationDone()
                {
                    if (EditorApplication.isCompiling) return;
                    EditorApplication.update -= CheckCompilationDone;
                    runtime.PublishEditorState(EditorBridgeState.Ready);
                }

                EditorApplication.update += CheckCompilationDone;
            };
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                runtime.PublishEditorState(EditorBridgeState.Reloading);
                runtime.Shutdown();
            };

            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                {
                    runtime.PublishEditorState(EditorBridgeState.EnteringPlayMode);
                }
                else if (state == PlayModeStateChange.ExitingPlayMode)
                {
                    runtime.PublishEditorState(EditorBridgeState.ExitingPlayMode);
                }
                else if (state == PlayModeStateChange.EnteredPlayMode
                         || state == PlayModeStateChange.EnteredEditMode)
                {
                    runtime.PublishEditorState(EditorBridgeState.Ready);
                }
            };

            EditorApplication.delayCall += () =>
            {
                PluginLogger.InitializeMainThreadState();
                runtime.PublishEditorState(EditorBridgeState.Ready);
            };
            EditorApplication.quitting += runtime.Shutdown;
        }
    }
}
