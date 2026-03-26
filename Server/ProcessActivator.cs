using System.Diagnostics;

namespace UnityMcpServer;

/// <summary>
/// macOS で osascript を使い、指定プロセスを前面に出すユーティリティ。
/// App Nap による Unity Editor のスロットリングを防ぐために使用する。
/// 他プラットフォームでは no-op。すべての操作は best-effort でサイレントに失敗する。
/// </summary>
internal static class ProcessActivator
{
    private const int OsascriptTimeoutMs = 2000;

    public static bool IsSupported => OperatingSystem.IsMacOS();

    public static async Task<int> GetFrontmostPidAsync()
    {
        if (!IsSupported) return 0;

        try
        {
            var output = await RunOsascriptAsync(
                "tell application \"System Events\" to get unix id of first process whose frontmost is true");
            if (int.TryParse(output?.Trim(), out var pid))
                return pid;
        }
        catch
        {
            // best-effort
        }

        return 0;
    }

    public static async Task SetFrontmostAsync(int pid)
    {
        if (!IsSupported || pid <= 0) return;

        try
        {
            await RunOsascriptAsync(
                $"tell application \"System Events\" to set frontmost of (first process whose unix id is {pid}) to true");
        }
        catch
        {
            // best-effort
        }
    }

    private static async Task<string?> RunOsascriptAsync(string script)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "osascript",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add(script);

        process.Start();

        using var cts = new CancellationTokenSource(OsascriptTimeoutMs);
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            return null;
        }
    }
}
