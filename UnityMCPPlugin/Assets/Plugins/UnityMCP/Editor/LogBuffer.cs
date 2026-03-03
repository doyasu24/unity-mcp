using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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

        internal static ReadConsolePayload Read(
            int maxEntries,
            HashSet<string> logTypes,
            Regex messageRegex,
            int stackTraceLines,
            bool deduplicate,
            int offset)
        {
            lock (Gate)
            {
                // Phase 1: Apply message_pattern filter and compute type_summary
                var summaryLog = 0;
                var summaryWarning = 0;
                var summaryError = 0;
                var summaryAssert = 0;
                var summaryException = 0;

                var afterMessageFilter = new List<ConsoleEntry>(Entries.Count);
                for (var i = 0; i < Entries.Count; i += 1)
                {
                    var entry = Entries[i];
                    if (messageRegex != null && !messageRegex.IsMatch(entry.Message))
                    {
                        continue;
                    }

                    afterMessageFilter.Add(entry);

                    switch (entry.Type)
                    {
                        case ConsoleLogTypes.Log: summaryLog += 1; break;
                        case ConsoleLogTypes.Warning: summaryWarning += 1; break;
                        case ConsoleLogTypes.Error: summaryError += 1; break;
                        case ConsoleLogTypes.Assert: summaryAssert += 1; break;
                        case ConsoleLogTypes.Exception: summaryException += 1; break;
                    }
                }

                var typeSummary = new TypeSummary(summaryLog, summaryWarning, summaryError, summaryAssert, summaryException);

                // Phase 2: Apply log_type filter
                List<ConsoleEntry> afterTypeFilter;
                if (logTypes != null)
                {
                    afterTypeFilter = new List<ConsoleEntry>(afterMessageFilter.Count);
                    for (var i = 0; i < afterMessageFilter.Count; i += 1)
                    {
                        if (logTypes.Contains(afterMessageFilter[i].Type))
                        {
                            afterTypeFilter.Add(afterMessageFilter[i]);
                        }
                    }
                }
                else
                {
                    afterTypeFilter = afterMessageFilter;
                }

                // Phase 3: Deduplicate consecutive identical entries
                List<ConsoleEntry> afterDedup;
                if (deduplicate)
                {
                    afterDedup = new List<ConsoleEntry>(afterTypeFilter.Count);
                    for (var i = 0; i < afterTypeFilter.Count; i += 1)
                    {
                        var current = afterTypeFilter[i];
                        if (afterDedup.Count > 0)
                        {
                            var last = afterDedup[afterDedup.Count - 1];
                            if (last.Type == current.Type && last.Message == current.Message)
                            {
                                afterDedup[afterDedup.Count - 1] = new ConsoleEntry(
                                    last.Type, last.Message, last.StackTrace, (last.Count ?? 1) + 1);
                                continue;
                            }
                        }

                        afterDedup.Add(new ConsoleEntry(current.Type, current.Message, current.StackTrace, 1));
                    }
                }
                else
                {
                    afterDedup = afterTypeFilter;
                }

                var totalCount = afterDedup.Count;

                // Phase 4: Offset + max_entries (take from end)
                var available = totalCount - offset;
                if (available < 0)
                {
                    available = 0;
                }

                var take = maxEntries < available ? maxEntries : available;
                var start = totalCount - offset - take;
                if (start < 0)
                {
                    start = 0;
                }

                // Phase 5: Build result entries with stack trace truncation
                var result = new List<ConsoleEntry>(take);
                for (var i = start; i < start + take; i += 1)
                {
                    var entry = afterDedup[i];
                    var stackTrace = stackTraceLines > 0 ? TruncateStackTrace(entry.StackTrace, stackTraceLines) : null;
                    var count = deduplicate ? entry.Count : (int?)null;
                    result.Add(new ConsoleEntry(entry.Type, entry.Message, stackTrace, count));
                }

                var truncated = offset + take < totalCount;
                int? nextOffset = truncated ? offset + take : (int?)null;

                return new ReadConsolePayload(result, result.Count, totalCount, truncated, nextOffset, typeSummary);
            }
        }

        private static string TruncateStackTrace(string stackTrace, int maxLines)
        {
            if (string.IsNullOrEmpty(stackTrace))
            {
                return stackTrace;
            }

            var lineCount = 0;
            for (var i = 0; i < stackTrace.Length; i += 1)
            {
                if (stackTrace[i] == '\n')
                {
                    lineCount += 1;
                    if (lineCount >= maxLines)
                    {
                        return stackTrace.Substring(0, i);
                    }
                }
            }

            return stackTrace;
        }

        internal static int Clear()
        {
            lock (Gate)
            {
                var removed = Entries.Count;
                Entries.Clear();
                return removed;
            }
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            if (PluginLogger.IsPluginLog(condition))
            {
                return;
            }

            lock (Gate)
            {
                Entries.Add(new ConsoleEntry(
                    ToWireLogType(type),
                    condition ?? string.Empty,
                    stackTrace ?? string.Empty,
                    null));

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
