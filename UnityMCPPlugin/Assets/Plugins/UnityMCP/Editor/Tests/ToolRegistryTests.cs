using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnityMcpPlugin.Tests
{
    [TestFixture]
    internal sealed class ToolRegistryTests
    {
        private ToolRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _registry = new ToolRegistry();
        }

        [Test]
        public void DiscoverAndRegister_FindsAllAutoDiscoverableTools()
        {
            // Phase 2: 14 tools, Phase 3: +13 inline tools extracted
            // GetEditorStateTool はコンストラクタ引数が必要なため自動発見されない
            _registry.DiscoverAndRegister();
            Assert.That(_registry.Count, Is.EqualTo(28));
        }

        [Test]
        public void TryGetHandler_UnknownTool_ReturnsFalse()
        {
            _registry.DiscoverAndRegister();
            var found = _registry.TryGetHandler("nonexistent_tool", out _);
            Assert.That(found, Is.False);
        }

        [Test]
        public void RegisterExplicit_AddsHandler()
        {
            var custom = new StubToolHandler("test_tool");
            _registry.RegisterExplicit(custom);

            Assert.That(_registry.TryGetHandler("test_tool", out var handler), Is.True);
            Assert.That(handler, Is.SameAs(custom));
            Assert.That(_registry.Count, Is.EqualTo(1));
        }

        [Test]
        public void RegisterExplicit_OverridesExisting()
        {
            var first = new StubToolHandler("my_tool");
            var second = new StubToolHandler("my_tool");
            _registry.RegisterExplicit(first);
            _registry.RegisterExplicit(second);

            Assert.That(_registry.TryGetHandler("my_tool", out var handler), Is.True);
            Assert.That(handler, Is.SameAs(second));
            Assert.That(_registry.Count, Is.EqualTo(1));
        }

        [Test]
        public void Count_ReflectsRegisteredHandlers()
        {
            Assert.That(_registry.Count, Is.EqualTo(0));

            _registry.RegisterExplicit(new StubToolHandler("tool_a"));
            Assert.That(_registry.Count, Is.EqualTo(1));

            _registry.RegisterExplicit(new StubToolHandler("tool_b"));
            Assert.That(_registry.Count, Is.EqualTo(2));
        }

        private sealed class StubToolHandler : IToolHandler
        {
            public StubToolHandler(string toolName)
            {
                ToolName = toolName;
            }

            public string ToolName { get; }
            public bool RequiresMainThread => false;

            public Task<object> ExecuteAsync(JObject parameters)
            {
                return Task.FromResult<object>(new { stub = true });
            }
        }
    }
}
