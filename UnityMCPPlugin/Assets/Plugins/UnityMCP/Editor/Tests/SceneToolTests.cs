using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityMcpPlugin.Tools;

namespace UnityMcpPlugin.Tests
{
    /// <summary>
    /// シーン・GameObject 関連ツールの直接呼び出しテスト。
    /// リファクタリング時の回帰検出が目的。
    ///
    /// NewScene は保存ダイアログを発生させるため一切使わず、
    /// 既存シーンのオブジェクトをクリアして再利用する。
    /// </summary>
    [TestFixture]
    internal sealed class SceneToolTests
    {
        private const string TestScenePath = "Assets/_UnityMcpTestScene.unity";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // シーンが無名 (untitled) だと SaveOpenScenes がダイアログを出すため、
            // 一時パスに保存してパスを確保する。NewScene は使わない。
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                EditorSceneManager.SaveScene(scene, TestScenePath);
            }
            else if (scene.isDirty)
            {
                EditorSceneManager.SaveScene(scene);
            }
        }

        [SetUp]
        public void SetUp()
        {
            // テスト毎にシーン内のオブジェクトをクリア
            var scene = SceneManager.GetActiveScene();
            foreach (var go in scene.GetRootGameObjects())
            {
                Object.DestroyImmediate(go);
            }

            // dirty フラグをクリア
            if (scene.isDirty)
            {
                EditorSceneManager.SaveScene(scene);
            }
        }

        [TearDown]
        public void TearDown()
        {
            // テスト後に dirty フラグをクリアして保存ダイアログを防ぐ
            var scene = SceneManager.GetActiveScene();
            if (scene.isDirty)
            {
                EditorSceneManager.SaveScene(scene);
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            // テスト用シーンファイルが作られた場合は削除
            if (AssetDatabase.LoadMainAssetAtPath(TestScenePath) != null)
            {
                AssetDatabase.DeleteAsset(TestScenePath);
            }
        }

        [Test]
        public void SceneHierarchyTool_EmptyScene_ReturnsResult()
        {
            var result = new SceneHierarchyTool().Execute(new JObject());
            Assert.That(result, Is.Not.Null);

            var jobj = result as JObject;
            Assert.That(jobj, Is.Not.Null);
            Assert.That(jobj["scene_name"], Is.Not.Null);
        }

        [Test]
        public void SceneHierarchyTool_WithGameObjects_ReturnsHierarchy()
        {
            var parent = new GameObject("Parent");
            var child = new GameObject("Child");
            child.transform.SetParent(parent.transform);

            var result = new SceneHierarchyTool().Execute(new JObject()) as JObject;

            Assert.That(result, Is.Not.Null);
            Assert.That(result["total_game_objects"]?.Value<int>(), Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void SceneHierarchyTool_WithRootPath_FiltersResults()
        {
            new GameObject("FilterTarget").transform.SetParent(null);
            var child = new GameObject("FilterChild");
            child.transform.SetParent(GameObject.Find("FilterTarget").transform);
            new GameObject("Other");

            var result = new SceneHierarchyTool().Execute(new JObject { ["root_path"] = "/FilterTarget" }) as JObject;
            Assert.That(result, Is.Not.Null);
        }

        [Test]
        public void SceneHierarchyTool_InvalidRootPath_ThrowsPluginException()
        {
            var ex = Assert.Throws<PluginException>(() =>
                new SceneHierarchyTool().Execute(new JObject { ["root_path"] = "/NonExistent" }));
            Assert.That(ex.Code, Is.EqualTo("ERR_OBJECT_NOT_FOUND"));
        }

        [Test]
        public void FindGameObjectsTool_ByName_FindsMatches()
        {
            new GameObject("SearchTarget");
            new GameObject("OtherObject");

            var result = (FindSceneGameObjectsPayload)new FindGameObjectsTool().Execute(new JObject { ["name"] = "SearchTarget" });
            Assert.That(result, Is.Not.Null);
            Assert.That(result.GameObjects.Count, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void FindGameObjectsTool_NoFilter_ThrowsPluginException()
        {
            var ex = Assert.Throws<PluginException>(() => new FindGameObjectsTool().Execute(new JObject()));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }

        [Test]
        public void ManageGameObjectTool_Create_CreatesGameObject()
        {
            new ManageGameObjectTool().Execute(new JObject
            {
                ["action"] = "create",
                ["name"] = "TestCreated"
            });

            Assert.That(GameObject.Find("TestCreated"), Is.Not.Null);
        }

        [Test]
        public void ManageGameObjectTool_Delete_DeletesGameObject()
        {
            new GameObject("ToDelete");

            new ManageGameObjectTool().Execute(new JObject
            {
                ["action"] = "delete",
                ["game_object_path"] = "/ToDelete"
            });

            Assert.That(GameObject.Find("ToDelete"), Is.Null);
        }

        [Test]
        public void ManageGameObjectTool_MissingAction_ThrowsPluginException()
        {
            var ex = Assert.Throws<PluginException>(() => new ManageGameObjectTool().Execute(new JObject()));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }

        [Test]
        public void ManageGameObjectTool_InvalidAction_ThrowsPluginException()
        {
            var ex = Assert.Throws<PluginException>(() =>
                new ManageGameObjectTool().Execute(new JObject { ["action"] = "invalid" }));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }

        [Test]
        public void ComponentInfoTool_ReturnsComponentInfo()
        {
            var go = new GameObject("CompTarget");
            go.AddComponent<BoxCollider>();

            var result = new ComponentInfoTool().Execute(new JObject { ["game_object_path"] = "/CompTarget" });
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.InstanceOf<JObject>());
        }

        [Test]
        public void ComponentInfoTool_MissingPath_ThrowsPluginException()
        {
            var ex = Assert.Throws<PluginException>(() => new ComponentInfoTool().Execute(new JObject()));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }

        [Test]
        public void ManageComponentTool_Add_AddsComponent()
        {
            var go = new GameObject("CompAddTarget");

            new ManageComponentTool().Execute(new JObject
            {
                ["action"] = "add",
                ["game_object_path"] = "/CompAddTarget",
                ["component_type"] = "BoxCollider"
            });

            Assert.That(go.GetComponent<BoxCollider>(), Is.Not.Null);
        }

        [Test]
        public void ManageComponentTool_MissingAction_ThrowsPluginException()
        {
            var ex = Assert.Throws<PluginException>(() =>
                new ManageComponentTool().Execute(new JObject { ["game_object_path"] = "/Something" }));
            Assert.That(ex.Code, Is.EqualTo("ERR_INVALID_PARAMS"));
        }

        [Test]
        public void CrossToolHelper_GetBool_WorksCorrectly()
        {
            Assert.That(ManageGameObjectTool.GetBool(new JObject { ["active"] = true }, "active"), Is.True);
            Assert.That(ManageGameObjectTool.GetBool(new JObject { ["active"] = false }, "active"), Is.False);
            Assert.That(ManageGameObjectTool.GetBool(new JObject(), "active"), Is.Null);
        }

        [Test]
        public void CrossToolHelper_CountDescendants_ReturnsCorrectCount()
        {
            var parent = new GameObject("P");
            new GameObject("C1").transform.SetParent(parent.transform);
            new GameObject("C2").transform.SetParent(parent.transform);
            new GameObject("GC").transform.SetParent(parent.transform.GetChild(0));

            Assert.That(ManageGameObjectTool.CountDescendants(parent.transform), Is.EqualTo(3));
        }

        [Test]
        public void CrossToolHelper_BuildComponentListing_ReturnsListing()
        {
            var go = new GameObject("ListingTarget");
            go.AddComponent<BoxCollider>();
            go.AddComponent<Rigidbody>();

            var components = go.GetComponents<Component>();
            var listing = ComponentInfoTool.BuildComponentListing(go, components);

            Assert.That(listing, Is.Not.Null);
            var comps = listing["components"] as JArray;
            Assert.That(comps, Is.Not.Null);
            // Transform + BoxCollider + Rigidbody = 3
            Assert.That(comps.Count, Is.EqualTo(3));
        }
    }
}
