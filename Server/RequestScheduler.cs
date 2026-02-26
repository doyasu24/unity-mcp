namespace UnityMcpServer;

internal sealed class RequestScheduler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _maxQueueSize;
    private int _queuedCount;
    private int _runningCount;

    public RequestScheduler(int maxQueueSize)
    {
        _maxQueueSize = maxQueueSize;
    }

    public async Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var queuedNow = Interlocked.Increment(ref _queuedCount);
        var runningNow = Volatile.Read(ref _runningCount);

        if (queuedNow + runningNow > _maxQueueSize)
        {
            Interlocked.Decrement(ref _queuedCount);
            throw new McpException(ErrorCodes.QueueFull, "Queue is full");
        }

        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        catch
        {
            Interlocked.Decrement(ref _queuedCount);
            throw;
        }

        Interlocked.Decrement(ref _queuedCount);
        Interlocked.Increment(ref _runningCount);

        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _runningCount);
            _gate.Release();
        }
    }
}
