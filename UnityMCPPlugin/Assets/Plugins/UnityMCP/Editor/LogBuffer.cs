using System.Collections.Generic;
using UnityEngine;

namespace UnityMcpPlugin
{
    internal static class LogBuffer
    {
        private const int MaxEntries = 5000;

        private static readonly object Gate = new();
        private static readonly List<ConsoleEntry> Entries = new(MaxEntries);

        private static bool _initialized;

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
        }

        internal static void Shutdown()
        {
            if (!_initialized)
            {
                return;
            }

            Application.logMessageReceivedThreaded -= OnLogMessageReceived;
            _initialized = false;
        }

        internal static ReadConsolePayload Read(int maxEntries)
        {
            lock (Gate)
            {
                var total = Entries.Count;
                var take = maxEntries < total ? maxEntries : total;
                var start = total - take;

                var sliced = new List<ConsoleEntry>(take);
                for (var i = start; i < total; i += 1)
                {
                    var current = Entries[i];
                    sliced.Add(new ConsoleEntry(current.Type, current.Message, current.StackTrace));
                }

                return new ReadConsolePayload(sliced, take, total > take);
            }
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            lock (Gate)
            {
                Entries.Add(new ConsoleEntry(
                    ToWireLogType(type),
                    condition ?? string.Empty,
                    stackTrace ?? string.Empty));

                if (Entries.Count > MaxEntries)
                {
                    Entries.RemoveRange(0, Entries.Count - MaxEntries);
                }
            }
        }

        private static string ToWireLogType(LogType type)
        {
            switch (type)
            {
                case LogType.Warning:
                    return "warning";
                case LogType.Error:
                    return "error";
                case LogType.Assert:
                    return "assert";
                case LogType.Exception:
                    return "exception";
                default:
                    return "log";
            }
        }
    }
}
