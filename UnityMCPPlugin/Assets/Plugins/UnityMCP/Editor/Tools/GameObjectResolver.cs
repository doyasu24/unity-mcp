using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMcpPlugin.Tools
{
    internal static class GameObjectResolver
    {
        internal static GameObject Resolve(string path)
        {
            var go = GameObject.Find(path);
            if (go != null)
            {
                return go;
            }

            return FindByTransformWalk(path);
        }

        internal static string GetHierarchyPath(GameObject go)
        {
            var t = go.transform;
            var path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }

            return "/" + path;
        }

        private static GameObject FindByTransformWalk(string path)
        {
            var normalized = path.TrimStart('/');
            if (string.IsNullOrEmpty(normalized))
            {
                return null;
            }

            var parts = normalized.Split('/');
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();

            GameObject root = null;
            foreach (var r in roots)
            {
                if (r.name == parts[0])
                {
                    root = r;
                    break;
                }
            }

            if (root == null)
            {
                return null;
            }

            if (parts.Length == 1)
            {
                return root;
            }

            var remaining = normalized.Substring(parts[0].Length + 1);
            var found = root.transform.Find(remaining);
            return found != null ? found.gameObject : null;
        }
    }
}
