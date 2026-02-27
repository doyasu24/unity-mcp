using System;

namespace UnityMcpServer;

internal sealed class HeartbeatMissState
{
    private readonly int _threshold;
    private int _misses;

    public HeartbeatMissState(int threshold)
    {
        _threshold = Math.Max(1, threshold);
    }

    public int Misses => _misses;

    public int Threshold => _threshold;

    public bool RegisterProbeResult(bool pongReceived)
    {
        if (pongReceived)
        {
            _misses = 0;
            return false;
        }

        _misses += 1;
        return _misses >= _threshold;
    }
}
