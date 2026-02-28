using System.Collections.Generic;

namespace UnityMcpPlugin.Tools
{
    internal static class SceneToolErrors
    {
        internal const string ObjectNotFound = "ERR_OBJECT_NOT_FOUND";
        internal const string ComponentIndexOutOfRange = "ERR_COMPONENT_INDEX_OUT_OF_RANGE";
        internal const string MissingScript = "ERR_MISSING_SCRIPT";
        internal const string ComponentTypeNotFound = "ERR_COMPONENT_TYPE_NOT_FOUND";
        internal const string ComponentTypeAmbiguous = "ERR_COMPONENT_TYPE_AMBIGUOUS";
        internal const string InvalidComponentType = "ERR_INVALID_COMPONENT_TYPE";
        internal const string ReferenceNotFound = "ERR_REFERENCE_NOT_FOUND";
        internal const string ComponentDependency = "ERR_COMPONENT_DEPENDENCY";
        internal const string PlayModeActive = "ERR_PLAY_MODE_ACTIVE";
    }

    internal static class SceneToolLimits
    {
        internal const int MaxDepthDefault = 10;
        internal const int MaxDepthMax = 50;
        internal const int MaxGameObjectsDefault = 1000;
        internal const int MaxGameObjectsMax = 10000;
        internal const int MaxArrayElementsDefault = 16;
        internal const int MaxArrayElementsMax = 64;
        internal const int MaxNestingDepth = 3;
        internal const int MaxFieldCount = 512;
    }

    internal static class ExcludedProperties
    {
        private static readonly HashSet<string> Names = new HashSet<string>
        {
            "m_Script",
            "m_ObjectHideFlags",
            "m_EditorHideFlags",
            "m_EditorClassIdentifier",
            "m_Name"
        };

        internal static bool Contains(string propertyName)
        {
            return Names.Contains(propertyName);
        }
    }
}
