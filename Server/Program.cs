using ConsoleAppFramework;
using Microsoft.Extensions.Logging;
using UnityMcpServer;

// 全パラメータにデフォルト値があるため、引数なしでも正常起動する
ConsoleApp.Run(args, async (int port = Constants.DefaultPort, LogLevel logLevel = LogLevel.Information, CancellationToken ct = default) =>
{
    if (port is < 1 or > 65535)
    {
        throw new ArgumentException($"--port must be between 1 and 65535 (got {port})");
    }

    await ServerHost.RunAsync(port, logLevel, ct);
});
