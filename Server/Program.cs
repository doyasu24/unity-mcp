namespace UnityMcpServer;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        return ServerHost.RunAsync(args);
    }
}
