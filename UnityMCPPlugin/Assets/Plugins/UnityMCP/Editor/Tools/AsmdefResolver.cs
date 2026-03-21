using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// Assembly Definition の名前 ↔ GUID 変換と参照解決を行うユーティリティ。
    /// </summary>
    internal static class AsmdefResolver
    {
        private const string GuidPrefix = "GUID:";

        /// <summary>
        /// Assembly 名から asmdef のパスと GUID を解決する。
        /// </summary>
        internal static (string path, string guid) ResolveByName(string assemblyName)
        {
            var path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName);
            if (string.IsNullOrEmpty(path))
            {
                throw new PluginException(AsmdefErrors.NotFound,
                    $"Assembly definition not found: {assemblyName}");
            }

            var guid = AssetDatabase.AssetPathToGUID(path);
            return (path, guid);
        }

        /// <summary>
        /// アセット GUID から asmdef のパスと名前を解決する。
        /// </summary>
        internal static (string path, string name) ResolveByGuid(string guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".asmdef"))
            {
                throw new PluginException(AsmdefErrors.NotFound,
                    $"Assembly definition not found for GUID: {guid}");
            }

            var json = System.IO.File.ReadAllText(path);
            var data = JsonUtility.FromJson<AsmdefData>(json);
            return (path, data.name);
        }

        /// <summary>
        /// name または guid パラメータから asmdef を解決する。排他チェック済みの前提。
        /// </summary>
        internal static (string path, string name, string guid) ResolveNameOrGuid(string nameParam, string guidParam)
        {
            if (!string.IsNullOrEmpty(guidParam))
            {
                var (path, name) = ResolveByGuid(guidParam);
                return (path, name, guidParam);
            }

            if (!string.IsNullOrEmpty(nameParam))
            {
                var (path, guid) = ResolveByName(nameParam);
                return (path, nameParam, guid);
            }

            throw new PluginException("ERR_INVALID_PARAMS", "name or guid is required");
        }

        /// <summary>
        /// reference または reference_guid パラメータから参照先 Assembly を解決する。
        /// 返り値は (名前, GUID) のタプル。
        /// </summary>
        internal static (string name, string guid) ResolveReference(string referenceParam, string referenceGuidParam)
        {
            if (!string.IsNullOrEmpty(referenceGuidParam))
            {
                var (_, name) = ResolveByGuid(referenceGuidParam);
                return (name, referenceGuidParam);
            }

            if (!string.IsNullOrEmpty(referenceParam))
            {
                var (_, guid) = ResolveByName(referenceParam);
                return (referenceParam, guid);
            }

            throw new PluginException("ERR_INVALID_PARAMS", "reference or reference_guid is required");
        }

        /// <summary>
        /// 既存の references 配列から Use GUIDs モードかどうかを検出する。
        /// 空配列の場合はデフォルト (true = GUID モード) を返す。
        /// </summary>
        internal static bool DetectUseGuids(string[] references)
        {
            if (references == null || references.Length == 0)
            {
                return true;
            }

            return references[0].StartsWith(GuidPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Assembly 名を "GUID:xxx" 形式に変換する。
        /// 入力が既に GUID 形式ならそのまま返す。
        /// </summary>
        internal static string ConvertToGuidRef(string nameOrGuid)
        {
            if (nameOrGuid.StartsWith(GuidPrefix, StringComparison.Ordinal))
            {
                return nameOrGuid;
            }

            var path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(nameOrGuid);
            if (string.IsNullOrEmpty(path))
            {
                throw new PluginException(AsmdefErrors.ReferenceNotFound,
                    $"Assembly definition not found: {nameOrGuid}");
            }

            var guid = AssetDatabase.AssetPathToGUID(path);
            return GuidPrefix + guid;
        }

        /// <summary>
        /// "GUID:xxx" 形式を Assembly 名に変換する。
        /// 入力が名前形式ならそのまま返す。
        /// </summary>
        internal static string ConvertToNameRef(string nameOrGuid)
        {
            if (!nameOrGuid.StartsWith(GuidPrefix, StringComparison.Ordinal))
            {
                return nameOrGuid;
            }

            var guid = nameOrGuid.Substring(GuidPrefix.Length);
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
            {
                return nameOrGuid; // 解決不能な場合は元の値を返す
            }

            var json = System.IO.File.ReadAllText(path);
            var data = JsonUtility.FromJson<AsmdefData>(json);
            return data.name;
        }

        /// <summary>
        /// 参照文字列を保存形式に変換する。
        /// useGuids が true なら GUID 形式、false なら名前形式に変換。
        /// </summary>
        internal static string ConvertRef(string nameOrGuid, bool useGuids)
        {
            return useGuids ? ConvertToGuidRef(nameOrGuid) : ConvertToNameRef(nameOrGuid);
        }

        /// <summary>
        /// 参照配列の各要素を保存形式に変換する。
        /// </summary>
        internal static string[] ConvertRefs(string[] references, bool useGuids)
        {
            if (references == null || references.Length == 0)
            {
                return Array.Empty<string>();
            }

            return references.Select(r => ConvertRef(r, useGuids)).ToArray();
        }

        /// <summary>
        /// 参照配列を名前と GUID のペアに解決する。レスポンス用。
        /// </summary>
        internal static AsmdefReferenceInfo[] ResolveReferenceInfos(string[] rawRefs)
        {
            if (rawRefs == null || rawRefs.Length == 0)
            {
                return Array.Empty<AsmdefReferenceInfo>();
            }

            return rawRefs.Select(r =>
            {
                if (r.StartsWith(GuidPrefix, StringComparison.Ordinal))
                {
                    var guid = r.Substring(GuidPrefix.Length);
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path))
                    {
                        return new AsmdefReferenceInfo(null, guid);
                    }

                    try
                    {
                        var json = System.IO.File.ReadAllText(path);
                        var data = JsonUtility.FromJson<AsmdefData>(json);
                        return new AsmdefReferenceInfo(data.name, guid);
                    }
                    catch
                    {
                        return new AsmdefReferenceInfo(null, guid);
                    }
                }
                else
                {
                    // 名前形式: 名前から GUID を解決
                    var asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(r);
                    var guid = string.IsNullOrEmpty(asmdefPath) ? "" : AssetDatabase.AssetPathToGUID(asmdefPath);
                    return new AsmdefReferenceInfo(r, guid);
                }
            }).ToArray();
        }

        /// <summary>
        /// 参照が既に配列に含まれているかを GUID/名前 両方でチェックする。
        /// </summary>
        internal static bool ContainsReference(string[] existingRefs, string refName, string refGuid)
        {
            if (existingRefs == null || existingRefs.Length == 0)
            {
                return false;
            }

            var guidRef = GuidPrefix + refGuid;
            foreach (var existing in existingRefs)
            {
                if (string.Equals(existing, refName, StringComparison.Ordinal))
                {
                    return true;
                }

                if (string.Equals(existing, guidRef, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 参照を配列から削除する。名前/GUID 両形式で検索する。
        /// </summary>
        internal static string[] RemoveReference(string[] existingRefs, string refName, string refGuid)
        {
            var guidRef = GuidPrefix + refGuid;
            var result = existingRefs.Where(r =>
                !string.Equals(r, refName, StringComparison.Ordinal) &&
                !string.Equals(r, guidRef, StringComparison.Ordinal)).ToArray();

            if (result.Length == existingRefs.Length)
            {
                throw new PluginException(AsmdefErrors.ReferenceNotInAssembly,
                    $"Reference '{refName}' not found in assembly");
            }

            return result;
        }
    }
}
