namespace UnityMcpPlugin
{
    internal sealed class EditorStateTracker
    {
        private readonly object _gate = new();

        private EditorBridgeState _state = EditorBridgeState.Ready;
        private ulong _seq;

        internal EditorStateChange Publish(EditorBridgeState nextState)
        {
            lock (_gate)
            {
                var changed = _state != nextState;
                _state = nextState;
                if (changed)
                {
                    _seq += 1;
                }

                return new(changed, _state, _seq);
            }
        }

        internal EditorSnapshot Snapshot(bool connected)
        {
            lock (_gate)
            {
                return new(connected, _state, _seq);
            }
        }

        internal EditorSnapshot IncrementSequenceForStatus(bool connected)
        {
            lock (_gate)
            {
                _seq += 1;
                return new(connected, _state, _seq);
            }
        }

        internal void ResetSequence()
        {
            lock (_gate)
            {
                _seq = 0;
            }
        }
    }
}
