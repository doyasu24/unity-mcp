using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace UnityMcpPlugin
{
    internal static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> Queue = new();

        private static int _mainThreadId = -1;
        private static bool _initialized;

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _mainThreadId = -1;
            EditorApplication.update += Drain;
        }

        internal static void Shutdown()
        {
            if (!_initialized)
            {
                return;
            }

            EditorApplication.update -= Drain;
            _initialized = false;

            while (Queue.TryDequeue(out var _))
            {
                // drop pending actions during shutdown
            }
        }

        internal static Task<T> InvokeAsync<T>(Func<T> func)
        {
            if (_mainThreadId >= 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                return Task.FromResult(func());
            }

            TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Enqueue(() =>
            {
                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        private static void Drain()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            while (Queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
