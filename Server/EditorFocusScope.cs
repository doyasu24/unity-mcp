namespace UnityMcpServer;

/// <summary>
/// Unity Editor を前面に出し、スコープ終了時に元のアプリへフォーカスを復元する RAII スコープ。
/// <c>await using var scope = await EditorFocusScope.ActivateAsync(pid);</c> で使用する。
/// </summary>
internal sealed class EditorFocusScope : IAsyncDisposable
{
    public static readonly EditorFocusScope Noop = new(0);

    private readonly int _previousFrontmostPid;

    private EditorFocusScope(int previousFrontmostPid)
    {
        _previousFrontmostPid = previousFrontmostPid;
    }

    /// <summary>
    /// Editor を前面に出し、元の frontmost PID を保存するスコープを作成する。
    /// プラットフォーム非対応・PID 無効・失敗時は <see cref="Noop"/> を返す。
    /// </summary>
    public static async Task<EditorFocusScope> ActivateAsync(int editorPid)
    {
        if (!ProcessActivator.IsSupported || editorPid <= 0)
            return Noop;

        try
        {
            var previousPid = await ProcessActivator.GetFrontmostPidAsync();
            await ProcessActivator.SetFrontmostAsync(editorPid);
            return new EditorFocusScope(previousPid);
        }
        catch
        {
            return Noop;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_previousFrontmostPid <= 0)
            return;

        try
        {
            await ProcessActivator.SetFrontmostAsync(_previousFrontmostPid);
        }
        catch
        {
            // best-effort
        }
    }
}
