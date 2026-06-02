using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityMcpPlugin.Tools;

namespace UnityMcpPlugin.Tests
{
    /// <summary>
    /// unload_scenes / restore_scenes の検証・パースロジックの単体テスト。
    ///
    /// 実際の退避/復元（EditorSceneManager の NewScene/OpenScene/RestoreSceneManagerSetup）は
    /// EditMode テストランナーの実行終了時 Undo（PerformUndoTask）と相性が悪く、テスト中のシーン
    /// 開閉が巻き戻される際に一時シーンファイルの復活やコミット済みシーンの破壊を招くため、ここでは
    /// 実行しない。代わりに Editor 状態に依存しない検証・パースロジック（ガードと scenes の解釈）を
    /// 直接テストする。退避→外部編集→復元の往復はライブ MCP E2E（CLAUDE.local.md 記載の手順）で検証する。
    /// </summary>
    [TestFixture]
    internal sealed class SceneSessionToolTests
    {
        [Test]
        public void ValidateUnloadableScenes_AllSavedAndClean_DoesNotThrow()
        {
            var scenes = new List<(string Path, bool IsDirty)>
            {
                ("Assets/A.unity", false),
                ("Assets/B.unity", false),
            };

            Assert.DoesNotThrow(() => UnloadScenesTool.ValidateUnloadableScenes(scenes));
        }

        [Test]
        public void ValidateUnloadableScenes_UntitledScene_ThrowsInvalidParams()
        {
            var scenes = new List<(string Path, bool IsDirty)>
            {
                ("Assets/A.unity", false),
                (string.Empty, false),
            };

            var ex = Assert.Throws<PluginException>(() => UnloadScenesTool.ValidateUnloadableScenes(scenes));
            Assert.AreEqual("ERR_INVALID_PARAMS", ex.Code);
        }

        [Test]
        public void ValidateUnloadableScenes_DirtyScene_ThrowsUnsavedChanges()
        {
            var scenes = new List<(string Path, bool IsDirty)>
            {
                ("Assets/A.unity", false),
                ("Assets/B.unity", true),
            };

            var ex = Assert.Throws<PluginException>(() => UnloadScenesTool.ValidateUnloadableScenes(scenes));
            Assert.AreEqual("ERR_UNSAVED_CHANGES", ex.Code);
        }

        [Test]
        public void ParseSceneEntries_MissingScenes_ThrowsInvalidParams()
        {
            var ex = Assert.Throws<PluginException>(() => RestoreScenesTool.ParseSceneEntries(new JObject()));
            Assert.AreEqual("ERR_INVALID_PARAMS", ex.Code);
        }

        [Test]
        public void ParseSceneEntries_EmptyScenesArray_ThrowsInvalidParams()
        {
            var parameters = new JObject { ["scenes"] = new JArray() };

            var ex = Assert.Throws<PluginException>(() => RestoreScenesTool.ParseSceneEntries(parameters));
            Assert.AreEqual("ERR_INVALID_PARAMS", ex.Code);
        }

        [Test]
        public void ParseSceneEntries_EntryWithoutPath_ThrowsInvalidParams()
        {
            var parameters = new JObject
            {
                ["scenes"] = new JArray { new JObject { ["is_active"] = true } },
            };

            var ex = Assert.Throws<PluginException>(() => RestoreScenesTool.ParseSceneEntries(parameters));
            Assert.AreEqual("ERR_INVALID_PARAMS", ex.Code);
        }

        [Test]
        public void ParseSceneEntries_AppliesDefaults_LoadedTrueActiveFalse()
        {
            var parameters = new JObject
            {
                ["scenes"] = new JArray { new JObject { ["path"] = "Assets/A.unity" } },
            };

            var entries = RestoreScenesTool.ParseSceneEntries(parameters);

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("Assets/A.unity", entries[0].Path);
            Assert.IsTrue(entries[0].IsLoaded, "is_loaded は未指定時 true");
            Assert.IsFalse(entries[0].IsActive, "is_active は未指定時 false");
        }

        [Test]
        public void ParseSceneEntries_PreservesProvidedFlags()
        {
            var parameters = new JObject
            {
                ["scenes"] = new JArray
                {
                    new JObject { ["path"] = "Assets/A.unity", ["is_active"] = true, ["is_loaded"] = true },
                    new JObject { ["path"] = "Assets/B.unity", ["is_active"] = false, ["is_loaded"] = false },
                },
            };

            var entries = RestoreScenesTool.ParseSceneEntries(parameters);

            Assert.AreEqual(2, entries.Count);
            Assert.IsTrue(entries[0].IsActive);
            Assert.IsTrue(entries[0].IsLoaded);
            Assert.IsFalse(entries[1].IsActive);
            Assert.IsFalse(entries[1].IsLoaded);
        }

        [Test]
        public void ResolveAnchorIndex_PicksFirstLoadedScene()
        {
            var entries = new List<SceneSetupEntry>
            {
                new SceneSetupEntry("Assets/A.unity", false, false),
                new SceneSetupEntry("Assets/B.unity", false, true),
                new SceneSetupEntry("Assets/C.unity", true, true),
            };

            Assert.AreEqual(1, RestoreScenesTool.ResolveAnchorIndex(entries));
        }

        [Test]
        public void ResolveAnchorIndex_NoLoadedScene_FallsBackToZero()
        {
            var entries = new List<SceneSetupEntry>
            {
                new SceneSetupEntry("Assets/A.unity", false, false),
                new SceneSetupEntry("Assets/B.unity", false, false),
            };

            Assert.AreEqual(0, RestoreScenesTool.ResolveAnchorIndex(entries));
        }
    }
}
