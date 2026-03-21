using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UnityMcpPlugin
{
    /// <summary>
    /// Plugin 側ツールハンドラーの共通インターフェース。
    /// TypeCache.GetTypesDerivedFrom で自動発見される。
    /// </summary>
    internal interface IToolHandler
    {
        /// <summary>Wire protocol ツール名 (例: "get_scene_hierarchy")</summary>
        string ToolName { get; }

        /// <summary>
        /// メインスレッドで実行する必要があるか。
        /// true の場合、ToolRegistry が MainThreadDispatcher.InvokeAsync でラップする。
        /// </summary>
        bool RequiresMainThread { get; }

        /// <summary>ツールを実行し結果を返す</summary>
        Task<object> ExecuteAsync(JObject parameters);
    }

    /// <summary>
    /// 同期・メインスレッド実行ツールの基底クラス。
    /// 大多数のツールはこの基底を使う。
    /// RequiresMainThread=true がデフォルト。ToolRegistry がメインスレッドへのディスパッチを行う。
    /// </summary>
    internal abstract class SyncToolHandler : IToolHandler
    {
        public abstract string ToolName { get; }

        public virtual bool RequiresMainThread => true;

        /// <summary>サブクラスが実装する同期実行メソッド</summary>
        public abstract object Execute(JObject parameters);

        public Task<object> ExecuteAsync(JObject parameters)
        {
            return Task.FromResult(Execute(parameters));
        }
    }

    /// <summary>
    /// 非同期ツールの基底クラス (RunTests, RefreshAssets, ClearConsole)。
    /// RequiresMainThread=false 固定。内部で必要に応じて MainThreadDispatcher を自分で呼ぶ。
    /// </summary>
    internal abstract class AsyncToolHandler : IToolHandler
    {
        public abstract string ToolName { get; }

        // 非同期ハンドラーは自前でスレッド管理を行う
        public bool RequiresMainThread => false;

        public abstract Task<object> ExecuteAsync(JObject parameters);
    }
}
