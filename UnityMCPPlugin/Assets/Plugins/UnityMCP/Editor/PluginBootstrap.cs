using UnityEditor;
using UnityEditor.Compilation;

namespace UnityMcpPlugin
{
    [InitializeOnLoad]
    internal static class PluginBootstrap
    {
        static PluginBootstrap()
        {
            var runtime = PluginRuntime.Instance;
            runtime.Initialize();

            CompilationPipeline.compilationStarted += _ => runtime.PublishEditorState(EditorBridgeState.Compiling);
            CompilationPipeline.compilationFinished += _ => runtime.PublishEditorState(EditorBridgeState.Ready);
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                runtime.PublishEditorState(EditorBridgeState.Reloading);
                runtime.Shutdown();
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
