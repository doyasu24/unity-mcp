using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityMcpPlugin
{
    internal sealed class JobExecutor
    {
        private const int MaxRetainedJobs = 256;
        private const int RetrieveTestListTimeoutMs = 5_000;
        private static readonly TimeSpan JobRetention = TimeSpan.FromMinutes(30);

        private const string SessionKeyJobIds = "UnityMCP_JobIds";
        private const string SessionKeyJobPrefix = "UnityMCP_Job_";

        private readonly object _gate = new();
        private readonly Dictionary<string, JobRecord> _jobs = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _executionGate;

        internal JobExecutor()
        {
            var hasRunning = RestoreFromSessionState();
            _executionGate = new SemaphoreSlim(hasRunning ? 0 : 1, 1);
        }

        internal string SubmitRunTestsJob(string mode, string filter, int timeoutMs = 0)
        {
            PruneJobs();

            var jobId = $"job-{Guid.NewGuid():N}";
            var record = new JobRecord(jobId, mode, filter, timeoutMs);

            lock (_gate)
            {
                _jobs[jobId] = record;
            }

            PersistJob(record);
            _ = RunTestsJobAsync(record);
            return jobId;
        }

        internal async Task<(bool Found, string State, RunTestsJobResult Result)> TryWaitForTerminalAsync(string jobId, int waitMs, CancellationToken cancellationToken)
        {
            PruneJobs();

            var record = FindJob(jobId);
            if (record == null)
            {
                return (false, null, null);
            }

            lock (record.Gate)
            {
                if (IsTerminal(record.State))
                {
                    record.Touch();
                    return (true, Wire.ToWireState(record.State), record.Result);
                }
            }

            await Task.WhenAny(record.TerminalTcs.Task, Task.Delay(waitMs, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            lock (record.Gate)
            {
                record.Touch();
                return (true, Wire.ToWireState(record.State), record.Result);
            }
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
                        record.ExplicitlyCancelled = true;
                        record.CancelTokenSource.Cancel();
                        record.State = JobState.Cancelled;
                        record.Result = RunTestsJobResult.Empty(record.Mode, record.Filter);
                        record.ActiveRunGuid = string.Empty;
                        record.Touch();
                        PersistJob(record);
                        status = "cancelled";
                        return true;
                    case JobState.Running:
                        record.ExplicitlyCancelled = true;
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
                MarkAborted(record);
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

                PersistJob(record);

                if (record.TimeoutMs > 0)
                {
                    record.CancelTokenSource.CancelAfter(record.TimeoutMs);
                }

                var runResult = await ExecuteRunTestsAsync(record, record.Mode, record.Filter, record.CancelTokenSource.Token);

                lock (record.Gate)
                {
                    if (record.CancelTokenSource.IsCancellationRequested)
                    {
                        var terminalState = record.ExplicitlyCancelled ? JobState.Cancelled : JobState.Timeout;
                        record.State = terminalState;
                        record.Result = RunTestsJobResult.Empty(record.Mode, record.Filter);
                        record.ActiveRunGuid = string.Empty;
                        record.Touch();
                        record.TerminalTcs.TrySetResult(true);
                        PersistJob(record);
                        return;
                    }

                    record.State = runResult.Summary.Failed > 0 ? JobState.Failed : JobState.Succeeded;
                    record.Result = runResult;
                    record.ActiveRunGuid = string.Empty;
                    record.Touch();
                    record.TerminalTcs.TrySetResult(true);
                }

                PersistJob(record);
            }
            catch (OperationCanceledException)
            {
                MarkAborted(record);
            }
            catch (Exception ex)
            {
                lock (record.Gate)
                {
                    record.State = JobState.Failed;
                    record.Result = BuildExceptionResult(record.Mode, record.Filter, ex);
                    record.ActiveRunGuid = string.Empty;
                    record.Touch();
                    record.TerminalTcs.TrySetResult(true);
                }

                PersistJob(record);
            }
            finally
            {
                _executionGate.Release();
                PruneJobs();
            }
        }

        private void MarkAborted(JobRecord record)
        {
            lock (record.Gate)
            {
                var terminalState = record.ExplicitlyCancelled ? JobState.Cancelled : JobState.Timeout;
                record.State = terminalState;
                record.Result = RunTestsJobResult.Empty(record.Mode, record.Filter);
                record.ActiveRunGuid = string.Empty;
                record.Touch();
                record.TerminalTcs.TrySetResult(true);
            }

            PersistJob(record);
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
            // Pre-check: retrieve test list and skip Execute if no leaf tests exist.
            // TestRunnerApi.Execute() never fires RunFinished when there are no tests,
            // causing the job to hang forever.
            var testListTcs = new TaskCompletionSource<ITestAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);
            TestRunnerApi preCheckApi = null;

            await MainThreadDispatcher.InvokeAsync(() =>
            {
                preCheckApi = ScriptableObject.CreateInstance<TestRunnerApi>();
                preCheckApi.RetrieveTestList(testMode, root => testListTcs.TrySetResult(root));
                return true;
            });

            using var preCheckCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preCheckCts.CancelAfter(RetrieveTestListTimeoutMs);

            var preCheckCancel = Task.Delay(Timeout.Infinite, preCheckCts.Token);
            var preCheckCompleted = await Task.WhenAny(testListTcs.Task, preCheckCancel);

            await MainThreadDispatcher.InvokeAsync(() =>
            {
                if (preCheckApi != null) UnityEngine.Object.DestroyImmediate(preCheckApi);
                return true;
            });

            if (!ReferenceEquals(preCheckCompleted, testListTcs.Task))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            var testRoot = await testListTcs.Task;
            if (!HasLeafTests(testRoot))
            {
                return;
            }

            // Actual test execution
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
                var canceledRun = Task.Delay(Timeout.Infinite, cancellationToken);
                var completedRun = await Task.WhenAny(completion.Task, canceledRun);
                if (!ReferenceEquals(completedRun, completion.Task))
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

        private static bool HasLeafTests(ITestAdaptor node)
        {
            if (node == null)
            {
                return false;
            }

            if (!node.IsSuite)
            {
                return true;
            }

            if (node.Children == null)
            {
                return false;
            }

            foreach (var child in node.Children)
            {
                if (HasLeafTests(child))
                {
                    return true;
                }
            }

            return false;
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
                var jobId = recordsToRemove[i].JobId;
                _ = MainThreadDispatcher.InvokeAsync(() =>
                {
                    SessionState.EraseString($"{SessionKeyJobPrefix}{jobId}");
                    return true;
                });
                recordsToRemove[i].CancelTokenSource.Dispose();
            }

            PersistJobIndex();
        }

        private static bool IsTerminal(JobState state)
        {
            return state == JobState.Succeeded ||
                   state == JobState.Failed ||
                   state == JobState.Timeout ||
                   state == JobState.Cancelled;
        }

        private bool RestoreFromSessionState()
        {
            var idsRaw = SessionState.GetString(SessionKeyJobIds, "");
            if (string.IsNullOrEmpty(idsRaw))
            {
                return false;
            }

            var hasRunning = false;
            var ids = idsRaw.Split(',');
            foreach (var id in ids)
            {
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var json = SessionState.GetString($"{SessionKeyJobPrefix}{id}", "");
                if (string.IsNullOrEmpty(json))
                {
                    continue;
                }

                PersistedJob persisted;
                try
                {
                    persisted = JsonConvert.DeserializeObject<PersistedJob>(json);
                }
                catch
                {
                    continue;
                }

                if (persisted == null)
                {
                    continue;
                }

                var state = (JobState)persisted.State;
                var record = JobRecord.Restore(id, persisted.Mode ?? "", persisted.Filter ?? "", state, persisted.Result, persisted.TimeoutMs);

                lock (_gate)
                {
                    _jobs[id] = record;
                }

                if (state == JobState.Running || state == JobState.Queued)
                {
                    hasRunning = true;
                }
            }

            return hasRunning;
        }

        private void PersistJob(JobRecord record)
        {
            RunTestsJobResult result;
            int stateInt;
            lock (record.Gate)
            {
                stateInt = (int)record.State;
                result = IsTerminal(record.State) ? record.Result : null;
            }

            var data = new PersistedJob
            {
                State = stateInt,
                Mode = record.Mode,
                Filter = record.Filter,
                Result = result,
                TimeoutMs = record.TimeoutMs,
            };

            var json = JsonConvert.SerializeObject(data);
            var key = $"{SessionKeyJobPrefix}{record.JobId}";
            _ = MainThreadDispatcher.InvokeAsync(() =>
            {
                SessionState.SetString(key, json);
                return true;
            });
            PersistJobIndex();
        }

        private void PersistJobIndex()
        {
            string ids;
            lock (_gate)
            {
                ids = string.Join(",", _jobs.Keys);
            }

            _ = MainThreadDispatcher.InvokeAsync(() =>
            {
                SessionState.SetString(SessionKeyJobIds, ids);
                return true;
            });
        }

        private const int DefaultRestoredJobTimeoutMs = 300_000;

        internal void ReattachRunningJobs()
        {
            List<JobRecord> running;
            lock (_gate)
            {
                running = new List<JobRecord>();
                foreach (var pair in _jobs)
                {
                    if (pair.Value.State == JobState.Running)
                    {
                        running.Add(pair.Value);
                    }
                }
            }

            if (running.Count == 0)
            {
                return;
            }

            foreach (var record in running)
            {
                var testApi = ScriptableObject.CreateInstance<TestRunnerApi>();
                var callback = new RestoredRunCallback(this, record, testApi);
                testApi.RegisterCallbacks(callback);

                var timeoutMs = record.TimeoutMs > 0 ? record.TimeoutMs : DefaultRestoredJobTimeoutMs;
                _ = TimeoutRestoredJobAsync(record, callback, testApi, timeoutMs);
            }

            PluginLogger.DevInfo("Reattached test callbacks for restored running jobs", ("count", running.Count));
        }

        private async Task TimeoutRestoredJobAsync(JobRecord record, RestoredRunCallback callback, TestRunnerApi testApi, int timeoutMs)
        {
            try
            {
                await Task.Delay(timeoutMs, record.CancelTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                // CTS was cancelled — either TryCancel or RunFinished already handled it
                lock (record.Gate)
                {
                    if (IsTerminal(record.State))
                    {
                        return;
                    }

                    var terminalState = record.ExplicitlyCancelled ? JobState.Cancelled : JobState.Timeout;
                    record.State = terminalState;
                    record.Result = RunTestsJobResult.Empty(record.Mode, record.Filter);
                    record.ActiveRunGuid = string.Empty;
                    record.Touch();
                    record.TerminalTcs.TrySetResult(true);
                }

                PersistJob(record);
                CleanupRestoredJob(callback, testApi);
                return;
            }

            // Delay completed without cancellation → timeout
            lock (record.Gate)
            {
                if (IsTerminal(record.State))
                {
                    return;
                }

                record.State = JobState.Timeout;
                record.Result = RunTestsJobResult.Empty(record.Mode, record.Filter);
                record.ActiveRunGuid = string.Empty;
                record.Touch();
                record.TerminalTcs.TrySetResult(true);
            }

            PersistJob(record);
            CleanupRestoredJob(callback, testApi);
        }

        private void CleanupRestoredJob(RestoredRunCallback callback, TestRunnerApi testApi)
        {
            _ = MainThreadDispatcher.InvokeAsync(() =>
            {
                TestRunnerApi.UnregisterTestCallback(callback);
                if (testApi != null)
                {
                    UnityEngine.Object.DestroyImmediate(testApi);
                }
                return true;
            });

            try
            {
                _executionGate.Release();
            }
            catch (SemaphoreFullException)
            {
                // gate already released
            }
        }

        private sealed class PersistedJob
        {
            [JsonProperty("s")] public int State;
            [JsonProperty("m")] public string Mode;
            [JsonProperty("f")] public string Filter;
            [JsonProperty("r")] public RunTestsJobResult Result;
            [JsonProperty("t")] public int TimeoutMs;
        }

        private sealed class RestoredRunCallback : ICallbacks
        {
            private readonly JobExecutor _executor;
            private readonly JobRecord _record;
            private readonly TestRunnerApi _testApi;

            internal RestoredRunCallback(JobExecutor executor, JobRecord record, TestRunnerApi testApi)
            {
                _executor = executor;
                _record = record;
                _testApi = testApi;
            }

            public void RunStarted(ITestAdaptor testsToRun) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                lock (_record.Gate)
                {
                    if (IsTerminal(_record.State))
                    {
                        return;
                    }
                }

                var aggregate = new RunAggregation(_record.Mode, _record.Filter);
                MergeRunResult(result, aggregate);
                var runResult = aggregate.ToResult();

                lock (_record.Gate)
                {
                    _record.State = runResult.Summary.Failed > 0 ? JobState.Failed : JobState.Succeeded;
                    _record.Result = runResult;
                    _record.Touch();
                    _record.TerminalTcs.TrySetResult(true);
                }

                _executor.PersistJob(_record);

                // Cancel CTS to stop TimeoutRestoredJobAsync
                try
                {
                    _record.CancelTokenSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // already disposed
                }

                try
                {
                    _executor._executionGate.Release();
                }
                catch (SemaphoreFullException)
                {
                    // gate already released
                }

                _ = MainThreadDispatcher.InvokeAsync(() =>
                {
                    TestRunnerApi.UnregisterTestCallback(this);
                    if (_testApi != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_testApi);
                    }
                    return true;
                });
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result) { }
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
            internal JobRecord(string jobId, string mode, string filter, int timeoutMs = 0)
            {
                JobId = jobId;
                Mode = mode;
                Filter = filter;
                TimeoutMs = timeoutMs;
                State = JobState.Queued;
                Result = RunTestsJobResult.Empty(mode, filter);
                CancelTokenSource = new CancellationTokenSource();
                UpdatedAt = DateTimeOffset.UtcNow;
                ActiveRunGuid = string.Empty;
            }

            internal static JobRecord Restore(string jobId, string mode, string filter, JobState state, RunTestsJobResult result, int timeoutMs = 0)
            {
                var record = new JobRecord(jobId, mode, filter, timeoutMs);
                record.State = state;
                record.Result = result ?? RunTestsJobResult.Empty(mode, filter);
                return record;
            }

            internal string JobId { get; }
            internal string Mode { get; }
            internal string Filter { get; }
            internal int TimeoutMs { get; }
            internal object Gate { get; } = new();
            internal JobState State { get; set; }
            internal RunTestsJobResult Result { get; set; }
            internal CancellationTokenSource CancelTokenSource { get; }
            internal TaskCompletionSource<bool> TerminalTcs { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal DateTimeOffset UpdatedAt { get; private set; }
            internal string ActiveRunGuid { get; set; }
            internal bool ExplicitlyCancelled { get; set; }

            internal void Touch()
            {
                UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
    }
}
