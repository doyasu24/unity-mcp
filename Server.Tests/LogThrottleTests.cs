using UnityMcpServer;

namespace UnityMcpServer.Tests;

public sealed class LogThrottleTests
{
    [Fact]
    public void FirstCall_AlwaysEmits_WithZeroSuppressed()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new LogThrottle(TimeSpan.FromSeconds(30), () => now);

        var (shouldEmit, suppressed) = throttle.Check();

        Assert.True(shouldEmit);
        Assert.Equal(0, suppressed);
    }

    [Fact]
    public void CallsWithinWindow_AreSuppressed()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new LogThrottle(TimeSpan.FromSeconds(30), () => now);

        throttle.Check(); // first emit

        for (var i = 0; i < 5; i++)
        {
            var (shouldEmit, _) = throttle.Check();
            Assert.False(shouldEmit);
        }
    }

    [Fact]
    public void AfterWindowExpires_EmitsWithCorrectSuppressedCount()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new LogThrottle(TimeSpan.FromSeconds(30), () => now);

        throttle.Check(); // first emit

        // 3 calls within window → suppressed
        throttle.Check();
        throttle.Check();
        throttle.Check();

        // advance past the window
        now += TimeSpan.FromSeconds(31);

        var (shouldEmit, suppressed) = throttle.Check();
        Assert.True(shouldEmit);
        Assert.Equal(3, suppressed);
    }

    [Fact]
    public void SuppressedCount_ResetsAfterEmit()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new LogThrottle(TimeSpan.FromSeconds(30), () => now);

        throttle.Check(); // first emit

        throttle.Check(); // suppressed
        throttle.Check(); // suppressed

        now += TimeSpan.FromSeconds(31);
        throttle.Check(); // emits with suppressed=2

        // another call within new window → suppressed
        var (shouldEmit, _) = throttle.Check();
        Assert.False(shouldEmit);

        // advance past window again
        now += TimeSpan.FromSeconds(31);
        var (shouldEmit2, suppressed2) = throttle.Check();
        Assert.True(shouldEmit2);
        Assert.Equal(1, suppressed2);
    }

    [Fact]
    public void ExactWindowBoundary_Emits()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new LogThrottle(TimeSpan.FromSeconds(30), () => now);

        throttle.Check(); // first emit

        // advance exactly to the boundary
        now += TimeSpan.FromSeconds(30);

        var (shouldEmit, suppressed) = throttle.Check();
        Assert.True(shouldEmit);
        Assert.Equal(0, suppressed);
    }
}
