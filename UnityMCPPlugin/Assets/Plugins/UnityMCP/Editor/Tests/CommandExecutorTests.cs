using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMcpPlugin.Tests
{
    /// <summary>
    /// CommandExecutor のディスパッチ動作を検証する特性テスト。
    /// リファクタリング中の回帰検出が目的。
    /// </summary>
    [TestFixture]
    internal sealed class CommandExecutorTests
    {
        private CommandExecutor _executor;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            MainThreadDispatcher.Initialize();
        }

        [SetUp]
        public void SetUp()
        {
            // EditorSnapshot のスタブを提供
            _executor = new CommandExecutor(() => new EditorSnapshot(true, EditorBridgeState.Ready, 1));
        }

        [Test]
        public void UnknownTool_ThrowsPluginException()
        {
            var ex = Assert.ThrowsAsync<PluginException>(async () =>
                await _executor.ExecuteToolAsync("nonexistent_tool", new JObject()));

            Assert.That(ex.Code, Is.EqualTo("ERR_UNKNOWN_COMMAND"));
        }

        [Test]
        public async Task GetEditorState_ReturnsSnapshot()
        {
            var result = await _executor.ExecuteToolAsync(ToolNames.GetEditorState, new JObject());
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.InstanceOf<RuntimeStatePayload>());
        }

        [Test]
        public async Task GetPlayModeState_ReturnsState()
        {
            var result = await _executor.ExecuteToolAsync(ToolNames.GetPlayModeState, new JObject());
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.InstanceOf<PlayModeStatePayload>());
        }

        [Test]
        public async Task ReadConsole_ReturnsPayload()
        {
            var parameters = new JObject { ["max_entries"] = 5 };
            var result = await _executor.ExecuteToolAsync(ToolNames.ReadConsole, parameters);
            Assert.That(result, Is.Not.Null);
        }

        [Test]
        public void ReadConsole_InvalidMaxEntries_ThrowsPluginException()
        {
            var parameters = new JObject { ["max_entries"] = -1 };
            var ex = Assert.ThrowsAsync<PluginException>(async () =>
                await _executor.ExecuteToolAsync(ToolNames.ReadConsole, parameters));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }

        [Test]
        public void ReadConsole_InvalidRegex_ThrowsPluginException()
        {
            var parameters = new JObject { ["message_pattern"] = "[invalid" };
            var ex = Assert.ThrowsAsync<PluginException>(async () =>
                await _executor.ExecuteToolAsync(ToolNames.ReadConsole, parameters));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }

        [Test]
        public async Task ClearConsole_ReturnsPayload()
        {
            var result = await _executor.ExecuteToolAsync(ToolNames.ClearConsole, new JObject());
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.InstanceOf<ClearConsolePayload>());
        }

        [Test]
        public void ControlPlayMode_InvalidAction_ThrowsPluginException()
        {
            var parameters = new JObject { ["action"] = "invalid_action" };
            var ex = Assert.ThrowsAsync<PluginException>(async () =>
                await _executor.ExecuteToolAsync(ToolNames.ControlPlayMode, parameters));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }

        [Test]
        public async Task ListScenes_ReturnsPayload()
        {
            var parameters = new JObject();
            var result = await _executor.ExecuteToolAsync(ToolNames.ListScenes, parameters);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.InstanceOf<ListScenesPayload>());
        }

        [Test]
        public async Task FindAssets_ReturnsResults()
        {
            var parameters = new JObject { ["filter"] = "t:Scene" };
            var result = await _executor.ExecuteToolAsync(ToolNames.FindAssets, parameters);
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.InstanceOf<FindAssetsPayload>());
        }

        [Test]
        public void FindAssets_MissingFilter_ThrowsPluginException()
        {
            var parameters = new JObject();
            var ex = Assert.ThrowsAsync<PluginException>(async () =>
                await _executor.ExecuteToolAsync(ToolNames.FindAssets, parameters));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }

        [Test]
        public async Task GetSelection_ReturnsPayload()
        {
            var result = await _executor.ExecuteToolAsync(ToolNames.GetSelection, new JObject());
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.InstanceOf<GetSelectionPayload>());
        }

        [Test]
        public async Task GetSceneHierarchy_ReturnsResult()
        {
            var parameters = new JObject();
            var result = await _executor.ExecuteToolAsync(ToolNames.GetSceneHierarchy, parameters);
            Assert.That(result, Is.Not.Null);
        }

        [Test]
        public async Task FindSceneGameObjects_WithNameFilter_ReturnsResult()
        {
            var parameters = new JObject { ["name"] = "Main Camera" };
            var result = await _executor.ExecuteToolAsync(ToolNames.FindSceneGameObjects, parameters);
            Assert.That(result, Is.Not.Null);
        }

        [Test]
        public void FindSceneGameObjects_NoFilter_ThrowsPluginException()
        {
            var parameters = new JObject();
            var ex = Assert.ThrowsAsync<PluginException>(async () =>
                await _executor.ExecuteToolAsync(ToolNames.FindSceneGameObjects, parameters));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }
    }
}
