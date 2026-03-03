using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal static class PrefabGameObjectResolver
    {
        internal static GameObject Resolve(GameObject prefabRoot, string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                return prefabRoot;
            }

            var normalized = path.TrimStart('/');
            if (string.IsNullOrEmpty(normalized))
            {
                return prefabRoot;
            }

            var found = prefabRoot.transform.Find(normalized);
            return found != null ? found.gameObject : null;
        }

        internal static string GetRelativePath(GameObject prefabRoot, GameObject target)
        {
            if (target == prefabRoot)
            {
                return "";
            }

            var t = target.transform;
            var path = t.name;
            while (t.parent != null && t.parent.gameObject != prefabRoot)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }

            return "/" + path;
        }
    }
}
