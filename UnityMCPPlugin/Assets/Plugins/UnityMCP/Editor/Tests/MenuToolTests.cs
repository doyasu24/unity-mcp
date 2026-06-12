using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityMcpPlugin.Tools;

namespace UnityMcpPlugin.Tests
{
    /// <summary>
    /// メニューツールの Editor 非依存ロジック(ブロックリスト判定・一覧フィルタ/ページング・定義元絞り込み)の単体テスト。
    /// 実際のメニュー実行・属性列挙・定義元判定はライブ MCP E2E で検証する。
    /// </summary>
    [TestFixture]
    internal sealed class MenuToolTests
    {
        // テスト用に builtin 定義元でラップする(定義元を問わないケース向け)
        private static MenuItemRaw[] Raw(params string[] paths)
        {
            return paths.Select(p => new MenuItemRaw(p, MenuItemOrigins.Builtin)).ToArray();
        }

        [Test]
        public void IsBlocked_ExactMatch_ReturnsTrue()
        {
            Assert.That(MenuBlocklist.IsBlocked("File/Quit"), Is.True);
        }

        [Test]
        public void IsBlocked_SubmenuOfBlockedPrefix_ReturnsTrue()
        {
            Assert.That(MenuBlocklist.IsBlocked("Assets/Import Package/Custom Package..."), Is.True);
        }

        [Test]
        public void IsBlocked_CaseInsensitive()
        {
            Assert.That(MenuBlocklist.IsBlocked("file/quit"), Is.True);
        }

        [Test]
        public void IsBlocked_NonBlockedMenu_ReturnsFalse()
        {
            Assert.That(MenuBlocklist.IsBlocked("GameObject/Create Empty"), Is.False);
        }

        [Test]
        public void IsBlocked_DestructiveAndModalNativeMenus()
        {
            Assert.That(MenuBlocklist.IsBlocked("Assets/Delete"), Is.True);
            Assert.That(MenuBlocklist.IsBlocked("Assets/Reimport All"), Is.True);
            Assert.That(MenuBlocklist.IsBlocked("File/Open Recent Scene/Assets/Scenes/Main.unity"), Is.True);
        }

        [Test]
        public void IsBlocked_PrefixIsNotMatchedAsPlainSubstring()
        {
            // "File/Quitter" は "File/Quit" の前方一致(="File/Quit/")ではないためブロックしない
            Assert.That(MenuBlocklist.IsBlocked("File/Quitter"), Is.False);
        }

        [Test]
        public void IsBlocked_NullOrEmpty_ReturnsFalse()
        {
            Assert.That(MenuBlocklist.IsBlocked(null), Is.False);
            Assert.That(MenuBlocklist.IsBlocked(string.Empty), Is.False);
        }

        [TestCase("File/New Scene %n", "File/New Scene")]
        [TestCase("GameObject/Create Empty %#n", "GameObject/Create Empty")]
        [TestCase("Edit/Deselect All #d", "Edit/Deselect All")]
        [TestCase("GameObject/Toggle Active State &#a", "GameObject/Toggle Active State")]
        [TestCase("Assets/Properties... _&P", "Assets/Properties...")]
        [TestCase("Window/General/Undo History  %u", "Window/General/Undo History")]
        [TestCase("Edit/Project Settings...", "Edit/Project Settings...")]
        [TestCase("Assets/Open C# Project", "Assets/Open C# Project")]
        public void StripShortcut_RemovesTrailingHotkeyOnly(string input, string expected)
        {
            Assert.That(MenuPath.StripShortcut(input), Is.EqualTo(expected));
        }

        [Test]
        public void IsBlocked_AfterStrippingShortcut_MatchesDenyList()
        {
            // "File/New Scene %n" は素のままでは deny にマッチしないが、ショートカット除去後は一致する
            Assert.That(MenuBlocklist.IsBlocked("File/New Scene %n"), Is.False);
            Assert.That(MenuBlocklist.IsBlocked(MenuPath.StripShortcut("File/New Scene %n")), Is.True);
        }

        [Test]
        public void Select_NormalizesShortcutsThenBlocks()
        {
            // ショートカット付きのブロッキングメニューは正規化後に除外される
            var selection = MenuItemLister.Select(
                Raw("File/New Scene %n", "GameObject/Create Empty %#n"),
                null, includeBlocked: false, sourceFilter: null, offset: 0, maxResults: 100);

            Assert.That(selection.Items.Select(i => i.Path), Is.EquivalentTo(new[] { "GameObject/Create Empty" }));
        }

        [Test]
        public void Select_IncludeBlocked_ReturnsStrippedBlockedPath()
        {
            var selection = MenuItemLister.Select(
                Raw("File/New Scene %n"), null, includeBlocked: true, sourceFilter: null, offset: 0, maxResults: 100);

            var entry = selection.Items.Single();
            Assert.That(entry.Path, Is.EqualTo("File/New Scene"));
            Assert.That(entry.Blocked, Is.True);
        }

        [Test]
        public void Select_ExcludesBlockedByDefault()
        {
            var selection = MenuItemLister.Select(
                Raw("GameObject/Create Empty", "File/Quit"),
                null, includeBlocked: false, sourceFilter: null, offset: 0, maxResults: 100);

            Assert.That(selection.Items.Select(i => i.Path), Is.EquivalentTo(new[] { "GameObject/Create Empty" }));
            Assert.That(selection.TotalCount, Is.EqualTo(1));
        }

        [Test]
        public void Select_IncludeBlocked_ReturnsBlockedWithFlag()
        {
            var selection = MenuItemLister.Select(
                Raw("GameObject/Create Empty", "File/Quit"),
                null, includeBlocked: true, sourceFilter: null, offset: 0, maxResults: 100);

            Assert.That(selection.TotalCount, Is.EqualTo(2));
            var quit = selection.Items.Single(i => i.Path == "File/Quit");
            Assert.That(quit.Blocked, Is.True);
            var create = selection.Items.Single(i => i.Path == "GameObject/Create Empty");
            Assert.That(create.Blocked, Is.False);
        }

        [Test]
        public void Select_AppliesRegexPatternCaseInsensitive()
        {
            var selection = MenuItemLister.Select(
                Raw("GameObject/Create Empty", "Component/Mesh/Mesh Filter", "GameObject/Light"),
                "^gameobject/", includeBlocked: false, sourceFilter: null, offset: 0, maxResults: 100);

            Assert.That(selection.Items.Select(i => i.Path),
                Is.EquivalentTo(new[] { "GameObject/Create Empty", "GameObject/Light" }));
        }

        [Test]
        public void Select_DeduplicatesAndSortsOrdinally()
        {
            var selection = MenuItemLister.Select(
                Raw("B/two", "A/one", "B/two"),
                null, includeBlocked: false, sourceFilter: null, offset: 0, maxResults: 100);

            Assert.That(selection.Items.Select(i => i.Path).ToList(),
                Is.EqualTo(new[] { "A/one", "B/two" }));
            Assert.That(selection.TotalCount, Is.EqualTo(2));
        }

        [Test]
        public void Select_PagesWithOffsetAndMaxResults()
        {
            var selection = MenuItemLister.Select(
                Raw("A/1", "A/2", "A/3", "A/4", "A/5"),
                null, includeBlocked: false, sourceFilter: null, offset: 1, maxResults: 2);

            Assert.That(selection.Items.Select(i => i.Path).ToList(), Is.EqualTo(new[] { "A/2", "A/3" }));
            Assert.That(selection.TotalCount, Is.EqualTo(5));
            Assert.That(selection.Truncated, Is.True);
            Assert.That(selection.NextOffset, Is.EqualTo(3));
        }

        [Test]
        public void Select_LastPage_NotTruncated()
        {
            var selection = MenuItemLister.Select(
                Raw("A/1", "A/2", "A/3"),
                null, includeBlocked: false, sourceFilter: null, offset: 2, maxResults: 10);

            Assert.That(selection.Items.Select(i => i.Path).ToList(), Is.EqualTo(new[] { "A/3" }));
            Assert.That(selection.Truncated, Is.False);
            Assert.That(selection.NextOffset, Is.Null);
        }

        [Test]
        public void Select_InvalidRegex_Throws()
        {
            var ex = Assert.Throws<PluginException>(() =>
                MenuItemLister.Select(Raw("A/1"), "[unterminated", includeBlocked: false, sourceFilter: null, offset: 0, maxResults: 10));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }

        [Test]
        public void Select_OverlongPattern_Throws()
        {
            var overlong = new string('a', 1001);
            var ex = Assert.Throws<PluginException>(() =>
                MenuItemLister.Select(Raw("A/1"), overlong, includeBlocked: false, sourceFilter: null, offset: 0, maxResults: 10));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }

        [Test]
        public void Select_IncludesSourceOnEachEntry()
        {
            var items = new[]
            {
                new MenuItemRaw("Tools/My Tool", MenuItemOrigins.Project),
                new MenuItemRaw("Window/TextMeshPro/X", MenuItemOrigins.Package),
                new MenuItemRaw("GameObject/3D Object/Cube", MenuItemOrigins.Builtin),
            };
            var selection = MenuItemLister.Select(items, null, includeBlocked: false, sourceFilter: null, offset: 0, maxResults: 100);

            Assert.That(selection.Items.Single(i => i.Path == "Tools/My Tool").Source, Is.EqualTo(MenuItemOrigins.Project));
            Assert.That(selection.Items.Single(i => i.Path == "Window/TextMeshPro/X").Source, Is.EqualTo(MenuItemOrigins.Package));
            Assert.That(selection.Items.Single(i => i.Path == "GameObject/3D Object/Cube").Source, Is.EqualTo(MenuItemOrigins.Builtin));
        }

        [Test]
        public void Select_SourceFilter_ReturnsOnlyMatchingOrigin()
        {
            var items = new[]
            {
                new MenuItemRaw("Tools/My Tool", MenuItemOrigins.Project),
                new MenuItemRaw("Window/TextMeshPro/X", MenuItemOrigins.Package),
                new MenuItemRaw("GameObject/3D Object/Cube", MenuItemOrigins.Builtin),
            };
            var selection = MenuItemLister.Select(
                items, null, includeBlocked: false, sourceFilter: MenuItemOrigins.Project, offset: 0, maxResults: 100);

            Assert.That(selection.Items.Select(i => i.Path), Is.EquivalentTo(new[] { "Tools/My Tool" }));
            Assert.That(selection.TotalCount, Is.EqualTo(1));
        }

        [Test]
        public void Select_InvalidSource_Throws()
        {
            var ex = Assert.Throws<PluginException>(() =>
                MenuItemLister.Select(Raw("A/1"), null, includeBlocked: false, sourceFilter: "bogus", offset: 0, maxResults: 10));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }

        [Test]
        public void Select_DuplicatePath_PrefersProjectOrigin()
        {
            // 同一パスが builtin と project で定義された場合は project を採用する
            var items = new[]
            {
                new MenuItemRaw("Tools/Shared", MenuItemOrigins.Builtin),
                new MenuItemRaw("Tools/Shared", MenuItemOrigins.Project),
            };
            var selection = MenuItemLister.Select(items, null, includeBlocked: false, sourceFilter: null, offset: 0, maxResults: 100);

            Assert.That(selection.Items.Single().Source, Is.EqualTo(MenuItemOrigins.Project));
        }
    }
}
