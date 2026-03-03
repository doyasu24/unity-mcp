using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal sealed class ToolContext
    {
        internal static readonly ToolContext Scene = new ToolContext(null, null);

        private readonly GameObject _prefabRoot;
        private readonly string _prefabAssetPath;

        private ToolContext(GameObject prefabRoot, string prefabAssetPath)
        {
            _prefabRoot = prefabRoot;
            _prefabAssetPath = prefabAssetPath;
        }

        internal static ToolContext ForPrefab(GameObject prefabRoot, string prefabAssetPath)
        {
            return new ToolContext(prefabRoot, prefabAssetPath);
        }

        internal bool IsPrefabContext
        {
            get { return _prefabRoot != null; }
        }

        internal GameObject ResolveGameObject(string path)
        {
            if (IsPrefabContext)
            {
                return PrefabGameObjectResolver.Resolve(_prefabRoot, path);
            }

            return GameObjectResolver.Resolve(path);
        }

        internal string GetPath(GameObject go)
        {
            if (IsPrefabContext)
            {
                return PrefabGameObjectResolver.GetRelativePath(_prefabRoot, go);
            }

            return GameObjectResolver.GetHierarchyPath(go);
        }

        internal bool IsSamePrefabReference(string assetPath)
        {
            if (!IsPrefabContext || string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            return string.Equals(_prefabAssetPath, assetPath, System.StringComparison.Ordinal);
        }
    }
}
