using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// Unity Test Runner を使ってテストを実行するツール。
    /// Fire-and-forget パターン: テスト開始後に即座に応答し、Server 側がポーリングで完了を待つ。
    /// - 初回呼び出し: テスト開始、{ "status": "started" } を返す
    /// - 実行中の呼び出し: { "status": "running" } を返す
    /// - 完了後の呼び出し: キャッシュされたテスト結果を返す
    /// </summary>
    internal sealed class RunTestsTool : AsyncToolHandler
    {
        private const int DefaultRunTestsTimeoutMs = 300_000;
        private const int RetrieveTestListTimeoutMs = 5_000;
        private const int CompilationPollIntervalMs = 500;

        private static readonly object _gate = new();
        private static bool _isRunning;
        private static object _pendingResult;

        public override string ToolName => ToolNames.RunTests;

        /// <summary>テストが実行中かどうか。GetEditorStateTool から参照される。</summary>
        internal static bool IsRunning
        {
            get { lock (_gate) return _isRunning; }
        }

        public override Task<object> ExecuteAsync(JObject parameters)
        {
            // テスト実行中またはキャッシュされた結果がある場合はそちらを返す
            lock (_gate)
            {
                if (_isRunning)
                {
                    return Task.FromResult<object>(new RunTestsStatusPayload("running"));
                }

                if (_pendingResult != null)
                {
                    var result = _pendingResult;
                    _pendingResult = null;
                    return Task.FromResult(result);
                }
            }

            // パラメータ検証（新規実行時のみ）
            var mode = Payload.GetString(parameters, "mode") ?? RunTestsModes.All;
            if (!RunTestsModes.IsSupported(mode))
            {
                throw new PluginException(
                    "ERR_INVALID_PARAMS",
                    $"mode must be {RunTestsModes.All}|{RunTestsModes.Edit}|{RunTestsModes.Play}");
            }

            var testFullName = Payload.GetString(parameters, "test_full_name") ?? string.Empty;
            var testNamePattern = Payload.GetString(parameters, "test_name_pattern") ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(testFullName) && !string.IsNullOrWhiteSpace(testNamePattern))
            {
                throw new PluginException(
                    "ERR_INVALID_PARAMS",
                    "test_full_name and test_name_pattern are mutually exclusive");
            }

            var timeoutMs = Payload.GetInt(parameters, "timeout_ms");
            if (!timeoutMs.HasValue || timeoutMs.Value <= 0)
            {
                timeoutMs = DefaultRunTestsTimeoutMs;
            }

            // テスト開始（バックグラウンドで実行し、完了後にキャッシュに格納）
            lock (_gate)
            {
                _isRunning = true;
                _pendingResult = null;
            }

            _ = ExecuteAndCacheResultAsync(mode, testFullName, testNamePattern, timeoutMs.Value);

            return Task.FromResult<object>(new RunTestsStatusPayload("started"));
        }

        /// <summary>
        /// テストをバックグラウンドで実行し、結果をキャッシュに格納する。
        /// </summary>
        private static async Task ExecuteAndCacheResultAsync(
            string mode, string testFullName, string testNamePattern, int timeoutMs)
        {
            try
            {
                var result = await ExecuteRunTestsAsync(mode, testFullName, testNamePattern, timeoutMs);
                lock (_gate)
                {
                    _isRunning = false;
                    _pendingResult = result;
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _isRunning = false;
                    _pendingResult = BuildExceptionResult(mode, testFullName, testNamePattern, ex);
                }
            }
        }

        /// <summary>
        /// コンパイル完了までポーリングで待機する。全体タイムアウトの CancellationToken を共有する。
        /// </summary>
        private static async Task WaitForCompilationAsync(CancellationToken cancellationToken)
        {
            while (await MainThreadDispatcher.InvokeAsync(() => EditorApplication.isCompiling))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(CompilationPollIntervalMs, cancellationToken);
            }
        }

        private static async Task<RunTestsJobResult> ExecuteRunTestsAsync(
            string mode, string testFullName, string testNamePattern, int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var cancellationToken = cts.Token;

            try
            {
                // コンパイル完了を待ってからテストを開始する。
                // コンパイル中は TestRunnerApi.RetrieveTestList() が応答できないため。
                await WaitForCompilationAsync(cancellationToken);

                var aggregate = new RunAggregation(mode, testFullName, testNamePattern);

                if (string.Equals(mode, RunTestsModes.All, StringComparison.Ordinal))
                {
                    await RunSingleModeAsync(TestMode.EditMode, testFullName, testNamePattern, aggregate, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await RunSingleModeAsync(TestMode.PlayMode, testFullName, testNamePattern, aggregate, cancellationToken);
                }
                else
                {
                    var testMode = string.Equals(mode, RunTestsModes.Play, StringComparison.Ordinal)
                        ? TestMode.PlayMode
                        : TestMode.EditMode;
                    await RunSingleModeAsync(testMode, testFullName, testNamePattern, aggregate, cancellationToken);
                }

                return aggregate.ToResult();
            }
            catch (OperationCanceledException)
            {
                throw new PluginException("ERR_TIMEOUT", "run_tests timed out");
            }
            catch (PluginException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return BuildExceptionResult(mode, testFullName, testNamePattern, ex);
            }
        }

        private static async Task RunSingleModeAsync(
            TestMode testMode,
            string testFullName,
            string testNamePattern,
            RunAggregation aggregate,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(testNamePattern))
            {
                try
                {
                    _ = new Regex(testNamePattern);
                }
                catch (ArgumentException ex)
                {
                    throw new PluginException("ERR_INVALID_PARAMS",
                        $"test_name_pattern is not a valid regex: {ex.Message}");
                }
            }

            // テストリスト取得による事前チェック: マッチするリーフテストがない場合は
            // Execute() を呼ばない。TestRunnerApi.Execute() はテストが存在しないと
            // RunFinished を発火しないため、リクエストが永久にハングする。
            var testRoot = await RetrieveTestListWithTimeoutAsync(testMode, RetrieveTestListTimeoutMs, cancellationToken);
            if (!HasMatchingLeafTests(testRoot, testFullName, testNamePattern))
            {
                return;
            }

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

                if (!string.IsNullOrWhiteSpace(testFullName))
                {
                    testFilter.testNames = new[] { testFullName };
                }
                else if (!string.IsNullOrWhiteSpace(testNamePattern))
                {
                    testFilter.groupNames = new[] { testNamePattern };
                }

                var settings = new ExecutionSettings(testFilter)
                {
                    runSynchronously = false,
                };

                var guid = testApi.Execute(settings);
                return guid;
            });

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
            }

            MergeRunResult(root, aggregate);
        }

        private static async Task<ITestAdaptor> RetrieveTestListWithTimeoutAsync(
            TestMode testMode, int timeoutMs, CancellationToken cancellationToken)
        {
            var testListTcs = new TaskCompletionSource<ITestAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);
            TestRunnerApi preCheckApi = null;

            await MainThreadDispatcher.InvokeAsync(() =>
            {
                preCheckApi = ScriptableObject.CreateInstance<TestRunnerApi>();
                preCheckApi.RetrieveTestList(testMode, root => testListTcs.TrySetResult(root));
                return true;
            });

            using var preCheckCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preCheckCts.CancelAfter(timeoutMs);

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
                throw new PluginException(
                    "ERR_TEST_LIST_TIMEOUT",
                    $"RetrieveTestList did not respond within {timeoutMs}ms (testMode={testMode})");
            }

            return await testListTcs.Task;
        }

        private static bool HasMatchingLeafTests(ITestAdaptor node, string testFullName, string testNamePattern)
        {
            if (node == null) return false;

            if (!node.IsSuite)
            {
                if (!string.IsNullOrWhiteSpace(testFullName))
                {
                    return string.Equals(node.FullName, testFullName, StringComparison.Ordinal);
                }

                if (!string.IsNullOrWhiteSpace(testNamePattern))
                {
                    try
                    {
                        return Regex.IsMatch(node.FullName ?? string.Empty, testNamePattern);
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                }

                return true;
            }

            if (node.Children == null) return false;

            foreach (var child in node.Children)
            {
                if (HasMatchingLeafTests(child, testFullName, testNamePattern)) return true;
            }

            return false;
        }

        private static void RequestCancelRun(string runGuid)
        {
            if (string.IsNullOrEmpty(runGuid)) return;
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
            if (result == null) return;

            if (result.HasChildren)
            {
                foreach (var child in result.Children)
                {
                    CollectFailedLeafResults(child, failures);
                }

                return;
            }

            if (result.TestStatus != TestStatus.Failed) return;

            failures.Add(new FailedTest(
                result.FullName ?? result.Name ?? "unknown",
                result.Message ?? string.Empty,
                result.StackTrace ?? string.Empty));
        }

        private static RunTestsJobResult BuildExceptionResult(string mode, string testFullName, string testNamePattern, Exception ex)
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
                testFullName,
                testNamePattern);
        }

        private sealed class RunAggregation
        {
            internal RunAggregation(string mode, string testFullName, string testNamePattern)
            {
                Mode = mode;
                TestFullName = testFullName;
                TestNamePattern = testNamePattern;
            }

            internal string Mode { get; }
            internal string TestFullName { get; }
            internal string TestNamePattern { get; }
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
                    TestFullName,
                    TestNamePattern);
            }
        }

        private sealed class RunCallback : ICallbacks
        {
            internal RunCallback(TaskCompletionSource<ITestResultAdaptor> completion)
            {
                Completion = completion;
            }

            internal TaskCompletionSource<ITestResultAdaptor> Completion { get; }

            public void RunStarted(ITestAdaptor testsToRun) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                Completion.TrySetResult(result);
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result) { }
        }
    }
}
