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
            CompilationPipeline.compilationFinished += _ => runtime.PublishEditorState(EditorBridgeState.Ready);
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
