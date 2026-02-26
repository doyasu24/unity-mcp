using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class ServerHostConfigurationTests
{
    [Fact]
    public void ConfigureHostDefaults_SetsShutdownTimeoutTo300Milliseconds()
    {
        var builder = WebApplication.CreateBuilder();

        ServerHost.ConfigureHostDefaults(builder);

        using var provider = builder.Services.BuildServiceProvider();
        var hostOptions = provider.GetRequiredService<IOptions<HostOptions>>().Value;
        Assert.Equal(TimeSpan.FromMilliseconds(300), hostOptions.ShutdownTimeout);
    }

    [Fact]
    public void ConfigureHostDefaults_AddsMicrosoftWarningFilter()
    {
        var builder = WebApplication.CreateBuilder();

        ServerHost.ConfigureHostDefaults(builder);

        using var provider = builder.Services.BuildServiceProvider();
        var filterOptions = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        Assert.Contains(filterOptions.Rules, static rule =>
            string.Equals(rule.CategoryName, "Microsoft", StringComparison.Ordinal) &&
            rule.LogLevel == LogLevel.Warning);
    }
}
