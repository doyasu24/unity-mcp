using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityMcpPlugin
{
    internal sealed class JobExecutor
    {
        private const int MaxRetainedJobs = 256;
        private static readonly TimeSpan JobRetention = TimeSpan.FromMinutes(30);

        private readonly object _gate = new();
        private readonly Dictionary<string, JobRecord> _jobs = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _executionGate = new(1, 1);

        internal string SubmitRunTestsJob(string mode, string filter)
        {
            PruneJobs();

            var jobId = $"job-{Guid.NewGuid():N}";
            var record = new JobRecord(jobId, mode, filter);

            lock (_gate)
            {
                _jobs[jobId] = record;
            }

            _ = RunTestsJobAsync(record);
            return jobId;
        }

        internal bool TryGetJobStatus(string jobId, out string state, out RunTestsJobResult result)
        {
            PruneJobs();

            var record = FindJob(jobId);
            if (record == null)
            {
                state = null;
                result = null;
                return false;
            }

            lock (record.Gate)
            {
                state = Wire.ToWireState(record.State);
                result = record.Result;
                record.Touch();
            }

            return true;
        }

        internal bool TryCancel(string jobId, out string status)
        {
            PruneJobs();

            var record = FindJob(jobId);
            if (record == null)
            {
                status = null;
                return false;
            }

            string runGuid;
            lock (record.Gate)
            {
                runGuid = record.ActiveRunGuid;

                switch (record.State)
                {
                    case JobState.Queued:
                        record.CancelTokenSource.Cancel();
                        record.State = JobState.Cancelled;
                        record.Result = RunTestsJobResult.Empty(record.Mode, record.Filter);
                        record.ActiveRunGuid = string.Empty;
                        record.Touch();
                        status = "cancelled";
                        return true;
                    case JobState.Running:
                        record.CancelTokenSource.Cancel();
                        record.Touch();
                        status = "cancel_requested";
                        break;
                    default:
                        status = "rejected";
                        return true;
                }
            }

            RequestCancelRun(runGuid);
            return true;
        }

        private JobRecord FindJob(string jobId)
        {
            lock (_gate)
            {
                _jobs.TryGetValue(jobId, out var record);
                return record;
            }
        }

        private async Task RunTestsJobAsync(JobRecord record)
        {
            try
            {
                await _executionGate.WaitAsync(record.CancelTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                MarkCancelled(record);
                return;
            }

            try
            {
                lock (record.Gate)
                {
                    if (record.State == JobState.Cancelled)
                    {
                        return;
                    }

                    record.State = JobState.Running;
                    record.Touch();
                }

                var runResult = await ExecuteRunTestsAsync(record, record.Mode, record.Filter, record.CancelTokenSource.Token);

                lock (record.Gate)
                {
                    if (record.CancelTokenSource.IsCancellationRequested)
                    {
                        record.State = JobState.Cancelled;
                        record.Result = RunTestsJobResult.Empty(record.Mode, record.Filter);
                        record.ActiveRunGuid = string.Empty;
                        record.Touch();
                        return;
                    }

                    record.State = runResult.Summary.Failed > 0 ? JobState.Failed : JobState.Succeeded;
                    record.Result = runResult;
                    record.ActiveRunGuid = string.Empty;
                    record.Touch();
                }
            }
            catch (OperationCanceledException)
            {
                MarkCancelled(record);
            }
            catch (Exception ex)
            {
                lock (record.Gate)
                {
                    record.State = JobState.Failed;
                    record.Result = BuildExceptionResult(record.Mode, record.Filter, ex);
                    record.ActiveRunGuid = string.Empty;
                    record.Touch();
                }
            }
            finally
            {
                _executionGate.Release();
                PruneJobs();
            }
        }

        private static void MarkCancelled(JobRecord record)
        {
            lock (record.Gate)
            {
                record.State = JobState.Cancelled;
                record.Result = RunTestsJobResult.Empty(record.Mode, record.Filter);
                record.ActiveRunGuid = string.Empty;
                record.Touch();
            }
        }

        private async Task<RunTestsJobResult> ExecuteRunTestsAsync(
            JobRecord record,
            string mode,
            string filter,
            CancellationToken cancellationToken)
        {
            var aggregate = new RunAggregation(mode, filter);

            if (string.Equals(mode, RunTestsModes.All, StringComparison.Ordinal))
            {
                await RunSingleModeAsync(record, TestMode.EditMode, filter, aggregate, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await RunSingleModeAsync(record, TestMode.PlayMode, filter, aggregate, cancellationToken);
            }
            else
            {
                var testMode = string.Equals(mode, RunTestsModes.Play, StringComparison.Ordinal)
                    ? TestMode.PlayMode
                    : TestMode.EditMode;
                await RunSingleModeAsync(record, testMode, filter, aggregate, cancellationToken);
            }

            return aggregate.ToResult();
        }

        private async Task RunSingleModeAsync(
            JobRecord record,
            TestMode testMode,
            string filter,
            RunAggregation aggregate,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<ITestResultAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callback = new RunCallback(completion);
            TestRunnerApi testApi = null;

            var runGuid = await MainThreadDispatcher.InvokeAsync(() =>
            {
                testApi = ScriptableObject.CreateInstance<TestRunnerApi>();
                testApi.RegisterCallbacks(callback);

                var testFilter = new Filter
                {
                    testMode = testMode,
                };

                if (!string.IsNullOrWhiteSpace(filter))
                {
                    testFilter.testNames = new[] { filter };
                }

                var settings = new ExecutionSettings(testFilter)
                {
                    runSynchronously = false,
                };

                var guid = testApi.Execute(settings);
                return guid;
            });

            lock (record.Gate)
            {
                record.ActiveRunGuid = runGuid;
                record.Touch();
            }

            using var registration = cancellationToken.Register(() => RequestCancelRun(runGuid));

            ITestResultAdaptor root;
            try
            {
                var canceled = Task.Delay(Timeout.Infinite, cancellationToken);
                var completed = await Task.WhenAny(completion.Task, canceled);
                if (!ReferenceEquals(completed, completion.Task))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                root = await completion.Task;
            }
            finally
            {
                await MainThreadDispatcher.InvokeAsync(() =>
                {
                    TestRunnerApi.UnregisterTestCallback(callback);
                    if (testApi != null)
                    {
                        UnityEngine.Object.DestroyImmediate(testApi);
                    }

                    return true;
                });

                lock (record.Gate)
                {
                    if (string.Equals(record.ActiveRunGuid, runGuid, StringComparison.Ordinal))
                    {
                        record.ActiveRunGuid = string.Empty;
                    }

                    record.Touch();
                }
            }

            MergeRunResult(root, aggregate);
        }

        private static void RequestCancelRun(string runGuid)
        {
            if (string.IsNullOrEmpty(runGuid))
            {
                return;
            }

            _ = MainThreadDispatcher.InvokeAsync(() => TestRunnerApi.CancelTestRun(runGuid));
        }

        private static void MergeRunResult(ITestResultAdaptor result, RunAggregation aggregate)
        {
            aggregate.Total += result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
            aggregate.Passed += result.PassCount;
            aggregate.Failed += result.FailCount;
            aggregate.Skipped += result.SkipCount + result.InconclusiveCount;
            aggregate.DurationMs += (int)Math.Round(result.Duration * 1000.0, MidpointRounding.AwayFromZero);

            CollectFailedLeafResults(result, aggregate.FailedTests);
        }

        private static void CollectFailedLeafResults(ITestResultAdaptor result, List<FailedTest> failures)
        {
            if (result == null)
            {
                return;
            }

            if (result.HasChildren)
            {
                foreach (var child in result.Children)
                {
                    CollectFailedLeafResults(child, failures);
                }

                return;
            }

            if (result.TestStatus != TestStatus.Failed)
            {
                return;
            }

            failures.Add(new FailedTest(
                result.FullName ?? result.Name ?? "unknown",
                result.Message ?? string.Empty,
                result.StackTrace ?? string.Empty));
        }

        private static RunTestsJobResult BuildExceptionResult(string mode, string filter, Exception ex)
        {
            return new RunTestsJobResult(
                new TestSummary(1, 0, 1, 0, 0),
                new List<FailedTest>
                {
                    new FailedTest(
                        "run_tests",
                        ex.Message,
                        ex.StackTrace ?? string.Empty),
                },
                mode,
                filter);
        }

        private void PruneJobs()
        {
            List<JobRecord> recordsToRemove = null;

            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var pair in _jobs)
                {
                    var record = pair.Value;
                    lock (record.Gate)
                    {
                        if (!IsTerminal(record.State))
                        {
                            continue;
                        }

                        if (now - record.UpdatedAt <= JobRetention)
                        {
                            continue;
                        }
                    }

                    recordsToRemove ??= new List<JobRecord>();
                    recordsToRemove.Add(record);
                }

                if (_jobs.Count > MaxRetainedJobs)
                {
                    var overflow = _jobs.Count - MaxRetainedJobs;
                    if (overflow > 0)
                    {
                        var terminalRecords = new List<JobRecord>();
                        foreach (var pair in _jobs)
                        {
                            var record = pair.Value;
                            lock (record.Gate)
                            {
                                if (!IsTerminal(record.State))
                                {
                                    continue;
                                }

                                if (recordsToRemove != null && recordsToRemove.Contains(record))
                                {
                                    continue;
                                }

                                terminalRecords.Add(record);
                            }
                        }

                        terminalRecords.Sort(static (left, right) => left.UpdatedAt.CompareTo(right.UpdatedAt));
                        var removeCount = Math.Min(overflow, terminalRecords.Count);
                        if (removeCount > 0)
                        {
                            recordsToRemove ??= new List<JobRecord>();
                            for (var i = 0; i < removeCount; i += 1)
                            {
                                recordsToRemove.Add(terminalRecords[i]);
                            }
                        }
                    }
                }

                if (recordsToRemove != null)
                {
                    for (var i = 0; i < recordsToRemove.Count; i += 1)
                    {
                        _jobs.Remove(recordsToRemove[i].JobId);
                    }
                }
            }

            if (recordsToRemove == null)
            {
                return;
            }

            for (var i = 0; i < recordsToRemove.Count; i += 1)
            {
                recordsToRemove[i].CancelTokenSource.Dispose();
            }
        }

        private static bool IsTerminal(JobState state)
        {
            return state == JobState.Succeeded ||
                   state == JobState.Failed ||
                   state == JobState.Timeout ||
                   state == JobState.Cancelled;
        }

        private sealed class RunAggregation
        {
            internal RunAggregation(string mode, string filter)
            {
                Mode = mode;
                Filter = filter;
            }

            internal string Mode { get; }
            internal string Filter { get; }
            internal int Total { get; set; }
            internal int Passed { get; set; }
            internal int Failed { get; set; }
            internal int Skipped { get; set; }
            internal int DurationMs { get; set; }
            internal List<FailedTest> FailedTests { get; } = new();

            internal RunTestsJobResult ToResult()
            {
                return new RunTestsJobResult(
                    new TestSummary(Total, Passed, Failed, Skipped, DurationMs),
                    FailedTests,
                    Mode,
                    Filter);
            }
        }

        private sealed class RunCallback : ICallbacks
        {
            internal RunCallback(TaskCompletionSource<ITestResultAdaptor> completion)
            {
                Completion = completion;
            }

            internal TaskCompletionSource<ITestResultAdaptor> Completion { get; }

            public void RunStarted(ITestAdaptor testsToRun)
            {
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                Completion.TrySetResult(result);
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
            }
        }

        private sealed class JobRecord
        {
            internal JobRecord(string jobId, string mode, string filter)
            {
                JobId = jobId;
                Mode = mode;
                Filter = filter;
                State = JobState.Queued;
                Result = RunTestsJobResult.Empty(mode, filter);
                CancelTokenSource = new CancellationTokenSource();
                UpdatedAt = DateTimeOffset.UtcNow;
                ActiveRunGuid = string.Empty;
            }

            internal string JobId { get; }
            internal string Mode { get; }
            internal string Filter { get; }
            internal object Gate { get; } = new();
            internal JobState State { get; set; }
            internal RunTestsJobResult Result { get; set; }
            internal CancellationTokenSource CancelTokenSource { get; }
            internal DateTimeOffset UpdatedAt { get; private set; }
            internal string ActiveRunGuid { get; set; }

            internal void Touch()
            {
                UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
    }
}
