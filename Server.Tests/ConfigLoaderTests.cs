using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class ConfigLoaderTests
{
    [Fact]
    public void Parse_UsesDefaultPort_WhenNoPortArgument()
    {
        var config = ConfigLoader.Parse([]);

        Assert.Equal(Constants.DefaultPort, config.Port);
    }

    [Fact]
    public void Parse_ReadsPort_FromSeparatedArgument()
    {
        var config = ConfigLoader.Parse(["--port", "48123"]);

        Assert.Equal(48123, config.Port);
    }

    [Fact]
    public void Parse_ReadsPort_FromEqualsArgument()
    {
        var config = ConfigLoader.Parse(["--port=48124"]);

        Assert.Equal(48124, config.Port);
    }

    [Fact]
    public void Parse_ThrowsValidation_WhenPortValueMissing()
    {
        var ex = Assert.Throws<McpException>(() => ConfigLoader.Parse(["--port"]));

        Assert.Equal(ErrorCodes.ConfigValidation, ex.Code);
        Assert.Equal("--port requires a value", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    public void Parse_ThrowsValidation_WhenPortIsOutOfRange(string port)
    {
        var ex = Assert.Throws<McpException>(() => ConfigLoader.Parse(["--port", port]));

        Assert.Equal(ErrorCodes.ConfigValidation, ex.Code);
        Assert.Equal("--port must be between 1 and 65535", ex.Message);
    }
}
