using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class McpInitializeTests
{
    [Fact]
    public void BuildInitializeResult_IncludesProtocolAndServerInfo()
    {
        var result = McpHttpHandler.BuildInitializeResult("2025-03-26");

        Assert.Equal("2025-03-26", result["protocolVersion"]?.GetValue<string>());
        Assert.Equal(Constants.ServerName, result["serverInfo"]?["name"]?.GetValue<string>());
        Assert.NotNull(result["serverInfo"]?["version"]);
    }

    [Fact]
    public void BuildInitializeResult_InstructionsDescribeSceneEditFlow()
    {
        var result = McpHttpHandler.BuildInitializeResult("2025-03-26");

        var instructions = result["instructions"]?.GetValue<string>();

        // 接続クライアントへ自動注入される説明文に、シーン編集フローの要点が
        // 含まれることを保証する（将来の削除・弱体化に対する回帰ガード）。
        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.Contains("unload_scenes", instructions);
        Assert.Contains("restore_scenes", instructions);
        Assert.Contains("refresh_assets", instructions);
        Assert.Contains(".unity", instructions);
    }
}
