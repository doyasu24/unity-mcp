using System;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// asmdef JSON のシリアライズ/デシリアライズ用データクラス。
    /// JsonUtility.FromJson/ToJson で使用する。
    /// </summary>
    [Serializable]
    internal class AsmdefData
    {
        public string name = "";
        public string rootNamespace = "";
        public string[] references = Array.Empty<string>();
        public string[] includePlatforms = Array.Empty<string>();
        public string[] excludePlatforms = Array.Empty<string>();
        public bool allowUnsafeCode;
        public bool overrideReferences;
        public string[] precompiledReferences = Array.Empty<string>();
        public bool autoReferenced = true;
        public string[] defineConstraints = Array.Empty<string>();
        public AsmdefVersionDefine[] versionDefines = Array.Empty<AsmdefVersionDefine>();
        public bool noEngineReferences;
    }

    [Serializable]
    internal class AsmdefVersionDefine
    {
        public string name;
        public string expression;
        public string define;
    }
}
