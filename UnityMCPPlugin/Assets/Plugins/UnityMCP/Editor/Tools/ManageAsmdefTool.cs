using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal sealed class ManageAsmdefTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.ManageAsmdef;

        public override object Execute(JObject parameters)
        {
            var action = Payload.GetString(parameters, "action");
            if (!ManageAsmdefActions.IsSupported(action))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"action must be {ManageAsmdefActions.List}|{ManageAsmdefActions.Get}|{ManageAsmdefActions.Create}|{ManageAsmdefActions.Update}|{ManageAsmdefActions.Delete}|{ManageAsmdefActions.AddReference}|{ManageAsmdefActions.RemoveReference}");
            }

            // 変更系 action は Play Mode 中にブロック
            if (action != ManageAsmdefActions.List && action != ManageAsmdefActions.Get && EditorApplication.isPlaying)
            {
                throw new PluginException(SceneToolErrors.PlayModeActive,
                    "Cannot manage assembly definitions while in Play Mode. Use control_play_mode to stop playback first.");
            }

            switch (action)
            {
                case ManageAsmdefActions.List:
                    return ExecuteList(parameters);
                case ManageAsmdefActions.Get:
                    return ExecuteGet(parameters);
                case ManageAsmdefActions.Create:
                    return ExecuteCreate(parameters);
                case ManageAsmdefActions.Update:
                    return ExecuteUpdate(parameters);
                case ManageAsmdefActions.Delete:
                    return ExecuteDelete(parameters);
                case ManageAsmdefActions.AddReference:
                    return ExecuteAddReference(parameters);
                case ManageAsmdefActions.RemoveReference:
                    return ExecuteRemoveReference(parameters);
                default:
                    throw new PluginException("ERR_INVALID_PARAMS", $"Unknown action: {action}");
            }
        }

        private static AsmdefListPayload ExecuteList(JObject parameters)
        {
            var namePattern = Payload.GetString(parameters, "name_pattern");
            var maxResults = Payload.GetInt(parameters, "max_results") ?? ManageAsmdefLimits.MaxResultsDefault;
            var offset = Payload.GetInt(parameters, "offset") ?? 0;

            if (maxResults < 1) maxResults = 1;
            if (maxResults > ManageAsmdefLimits.MaxResultsMax) maxResults = ManageAsmdefLimits.MaxResultsMax;
            if (offset < 0) offset = 0;

            Regex nameRegex = null;
            if (!string.IsNullOrEmpty(namePattern))
            {
                try
                {
                    nameRegex = new Regex(namePattern, RegexOptions.IgnoreCase);
                }
                catch
                {
                    throw new PluginException("ERR_INVALID_PARAMS", $"Invalid regex pattern: {namePattern}");
                }
            }

            // AssetDatabase から全 asmdef を列挙（CompilationPipeline はコンパイル可能なもののみのため不使用）
            var guids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset");
            var entries = new List<AsmdefListEntry>();

            // sourceFiles カウント用に CompilationPipeline のアセンブリを名前→sourceFiles.Length のマップに
            var compiledAssemblies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var asm in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
            {
                compiledAssemblies[asm.name] = asm.sourceFiles?.Length ?? 0;
            }

            foreach (var assetGuid in guids)
            {
                var asmdefPath = AssetDatabase.GUIDToAssetPath(assetGuid);
                if (string.IsNullOrEmpty(asmdefPath) || !asmdefPath.EndsWith(".asmdef"))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(asmdefPath);
                    var data = JsonUtility.FromJson<AsmdefData>(json);

                    if (nameRegex != null && !nameRegex.IsMatch(data.name))
                    {
                        continue;
                    }

                    var useGuids = AsmdefResolver.DetectUseGuids(data.references);
                    compiledAssemblies.TryGetValue(data.name, out var sourceFileCount);

                    entries.Add(new AsmdefListEntry(
                        data.name,
                        assetGuid,
                        asmdefPath,
                        data.rootNamespace ?? "",
                        useGuids,
                        sourceFileCount,
                        data.references?.Length ?? 0));
                }
                catch
                {
                    // JSON パースに失敗した asmdef はスキップ
                }
            }

            // 名前順ソート
            entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

            var totalCount = entries.Count;
            var paged = entries.Skip(offset).Take(maxResults).ToArray();
            var truncated = offset + paged.Length < totalCount;
            int? nextOffset = truncated ? offset + paged.Length : null;

            return new AsmdefListPayload(paged, paged.Length, totalCount, truncated, nextOffset);
        }

        private static AsmdefGetPayload ExecuteGet(JObject parameters)
        {
            var nameParam = Payload.GetString(parameters, "name");
            var guidParam = Payload.GetString(parameters, "guid");
            ValidateExclusive(nameParam, guidParam, "name", "guid");

            var (path, asmName, asmGuid) = AsmdefResolver.ResolveNameOrGuid(nameParam, guidParam);

            var json = File.ReadAllText(path);
            var data = JsonUtility.FromJson<AsmdefData>(json);
            var useGuids = AsmdefResolver.DetectUseGuids(data.references);

            // CompilationPipeline から sourceFiles と defines を取得
            var sourceFiles = Array.Empty<string>();
            var defines = Array.Empty<string>();
            var compiledAsm = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .FirstOrDefault(a => a.name == data.name);
            if (compiledAsm != null)
            {
                sourceFiles = compiledAsm.sourceFiles ?? Array.Empty<string>();
                defines = compiledAsm.defines ?? Array.Empty<string>();
            }

            var references = AsmdefResolver.ResolveReferenceInfos(data.references);
            var versionDefines = (data.versionDefines ?? Array.Empty<AsmdefVersionDefine>())
                .Select(v => new AsmdefVersionDefineInfo(v.name, v.expression, v.define))
                .ToArray();

            return new AsmdefGetPayload(
                data.name,
                asmGuid,
                path,
                data.rootNamespace ?? "",
                references,
                useGuids,
                data.includePlatforms ?? Array.Empty<string>(),
                data.excludePlatforms ?? Array.Empty<string>(),
                data.allowUnsafeCode,
                data.autoReferenced,
                data.defineConstraints ?? Array.Empty<string>(),
                versionDefines,
                data.noEngineReferences,
                data.overrideReferences,
                data.precompiledReferences ?? Array.Empty<string>(),
                sourceFiles,
                defines);
        }

        private static AsmdefCreatePayload ExecuteCreate(JObject parameters)
        {
            // create では guid パラメータは不正
            var guidParam = Payload.GetString(parameters, "guid");
            if (!string.IsNullOrEmpty(guidParam))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "'guid' is not valid for 'create' action, use 'name' instead");
            }

            var name = Payload.GetString(parameters, "name");
            if (string.IsNullOrEmpty(name))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "name is required for 'create' action");
            }

            var directory = Payload.GetString(parameters, "directory");
            if (string.IsNullOrEmpty(directory))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "directory is required for 'create' action");
            }

            // 同名チェック
            var existingPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(name);
            if (!string.IsNullOrEmpty(existingPath))
            {
                throw new PluginException(AsmdefErrors.AlreadyExists,
                    $"Assembly definition already exists: {name} at {existingPath}");
            }

            var useGuids = Payload.GetBool(parameters, "use_guids") ?? true;

            var data = new AsmdefData
            {
                name = name,
                rootNamespace = Payload.GetString(parameters, "root_namespace") ?? "",
                allowUnsafeCode = Payload.GetBool(parameters, "allow_unsafe_code") ?? false,
                autoReferenced = Payload.GetBool(parameters, "auto_referenced") ?? true,
                noEngineReferences = Payload.GetBool(parameters, "no_engine_references") ?? false,
            };

            // references 配列
            var refsArray = parameters["references"] as JArray;
            if (refsArray != null && refsArray.Count > 0)
            {
                var refStrings = refsArray.Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                data.references = AsmdefResolver.ConvertRefs(refStrings, useGuids);
            }

            // プラットフォーム
            data.includePlatforms = ExtractStringArray(parameters, "include_platforms");
            data.excludePlatforms = ExtractStringArray(parameters, "exclude_platforms");
            data.defineConstraints = ExtractStringArray(parameters, "define_constraints");

            // ディレクトリ作成
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var filePath = Path.Combine(directory, name + ".asmdef");
            var json = JsonUtility.ToJson(data, true);
            File.WriteAllText(filePath, json);
            AssetDatabase.ImportAsset(filePath);

            var guid = AssetDatabase.AssetPathToGUID(filePath);
            return new AsmdefCreatePayload(name, guid, filePath);
        }

        private static AsmdefUpdatePayload ExecuteUpdate(JObject parameters)
        {
            var nameParam = Payload.GetString(parameters, "name");
            var guidParam = Payload.GetString(parameters, "guid");
            ValidateExclusive(nameParam, guidParam, "name", "guid");

            var (path, asmName, asmGuid) = AsmdefResolver.ResolveNameOrGuid(nameParam, guidParam);

            var json = File.ReadAllText(path);
            var data = JsonUtility.FromJson<AsmdefData>(json);
            var updatedFields = new List<string>();

            // 指定されたフィールドのみ上書き
            var rootNamespace = Payload.GetString(parameters, "root_namespace");
            if (rootNamespace != null)
            {
                data.rootNamespace = rootNamespace;
                updatedFields.Add("root_namespace");
            }

            var allowUnsafe = Payload.GetBool(parameters, "allow_unsafe_code");
            if (allowUnsafe.HasValue)
            {
                data.allowUnsafeCode = allowUnsafe.Value;
                updatedFields.Add("allow_unsafe_code");
            }

            var autoRef = Payload.GetBool(parameters, "auto_referenced");
            if (autoRef.HasValue)
            {
                data.autoReferenced = autoRef.Value;
                updatedFields.Add("auto_referenced");
            }

            var noEngine = Payload.GetBool(parameters, "no_engine_references");
            if (noEngine.HasValue)
            {
                data.noEngineReferences = noEngine.Value;
                updatedFields.Add("no_engine_references");
            }

            // references 全体置換
            if (parameters["references"] is JArray refsArray)
            {
                var useGuids = AsmdefResolver.DetectUseGuids(data.references);
                var refStrings = refsArray.Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                data.references = AsmdefResolver.ConvertRefs(refStrings, useGuids);
                updatedFields.Add("references");
            }

            // プラットフォーム
            if (parameters["include_platforms"] is JArray)
            {
                data.includePlatforms = ExtractStringArray(parameters, "include_platforms");
                updatedFields.Add("include_platforms");
            }

            if (parameters["exclude_platforms"] is JArray)
            {
                data.excludePlatforms = ExtractStringArray(parameters, "exclude_platforms");
                updatedFields.Add("exclude_platforms");
            }

            if (parameters["define_constraints"] is JArray)
            {
                data.defineConstraints = ExtractStringArray(parameters, "define_constraints");
                updatedFields.Add("define_constraints");
            }

            var updatedJson = JsonUtility.ToJson(data, true);
            File.WriteAllText(path, updatedJson);
            AssetDatabase.ImportAsset(path);

            return new AsmdefUpdatePayload(asmName, asmGuid, path, updatedFields.ToArray());
        }

        private static AsmdefDeletePayload ExecuteDelete(JObject parameters)
        {
            var nameParam = Payload.GetString(parameters, "name");
            var guidParam = Payload.GetString(parameters, "guid");
            ValidateExclusive(nameParam, guidParam, "name", "guid");

            var (path, asmName, asmGuid) = AsmdefResolver.ResolveNameOrGuid(nameParam, guidParam);

            var deleted = AssetDatabase.DeleteAsset(path);
            if (!deleted)
            {
                throw new PluginException("ERR_UNITY_EXECUTION",
                    $"Failed to delete assembly definition at: {path}");
            }

            return new AsmdefDeletePayload(asmName, asmGuid, path, true);
        }

        private static AsmdefReferenceChangePayload ExecuteAddReference(JObject parameters)
        {
            var nameParam = Payload.GetString(parameters, "name");
            var guidParam = Payload.GetString(parameters, "guid");
            ValidateExclusive(nameParam, guidParam, "name", "guid");

            var referenceParam = Payload.GetString(parameters, "reference");
            var referenceGuidParam = Payload.GetString(parameters, "reference_guid");
            ValidateExclusive(referenceParam, referenceGuidParam, "reference", "reference_guid");

            var (path, asmName, asmGuid) = AsmdefResolver.ResolveNameOrGuid(nameParam, guidParam);
            var (refName, refGuid) = AsmdefResolver.ResolveReference(referenceParam, referenceGuidParam);

            var json = File.ReadAllText(path);
            var data = JsonUtility.FromJson<AsmdefData>(json);

            // 重複チェック
            if (AsmdefResolver.ContainsReference(data.references, refName, refGuid))
            {
                throw new PluginException(AsmdefErrors.DuplicateReference,
                    $"Reference '{refName}' already exists in assembly '{asmName}'");
            }

            // 保存形式を検出して適切な形式で追加
            var useGuids = AsmdefResolver.DetectUseGuids(data.references);
            var refValue = useGuids ? "GUID:" + refGuid : refName;

            var refs = new List<string>(data.references) { refValue };
            data.references = refs.ToArray();

            var updatedJson = JsonUtility.ToJson(data, true);
            File.WriteAllText(path, updatedJson);
            AssetDatabase.ImportAsset(path);

            var addedRef = new AsmdefReferenceInfo(refName, refGuid);
            var allRefs = AsmdefResolver.ResolveReferenceInfos(data.references);

            return new AsmdefReferenceChangePayload(asmName, asmGuid, path, addedRef, null, allRefs);
        }

        private static AsmdefReferenceChangePayload ExecuteRemoveReference(JObject parameters)
        {
            var nameParam = Payload.GetString(parameters, "name");
            var guidParam = Payload.GetString(parameters, "guid");
            ValidateExclusive(nameParam, guidParam, "name", "guid");

            var referenceParam = Payload.GetString(parameters, "reference");
            var referenceGuidParam = Payload.GetString(parameters, "reference_guid");
            ValidateExclusive(referenceParam, referenceGuidParam, "reference", "reference_guid");

            var (path, asmName, asmGuid) = AsmdefResolver.ResolveNameOrGuid(nameParam, guidParam);
            var (refName, refGuid) = AsmdefResolver.ResolveReference(referenceParam, referenceGuidParam);

            var json = File.ReadAllText(path);
            var data = JsonUtility.FromJson<AsmdefData>(json);

            data.references = AsmdefResolver.RemoveReference(data.references, refName, refGuid);

            var updatedJson = JsonUtility.ToJson(data, true);
            File.WriteAllText(path, updatedJson);
            AssetDatabase.ImportAsset(path);

            var removedRef = new AsmdefReferenceInfo(refName, refGuid);
            var allRefs = AsmdefResolver.ResolveReferenceInfos(data.references);

            return new AsmdefReferenceChangePayload(asmName, asmGuid, path, null, removedRef, allRefs);
        }

        /// <summary>
        /// 排他パラメータのバリデーション。両方指定されていたらエラー。
        /// </summary>
        private static void ValidateExclusive(string paramA, string paramB, string nameA, string nameB)
        {
            if (!string.IsNullOrEmpty(paramA) && !string.IsNullOrEmpty(paramB))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"Specify either '{nameA}' or '{nameB}', not both");
            }
        }

        /// <summary>
        /// JObject から string[] を抽出するヘルパー。
        /// </summary>
        private static string[] ExtractStringArray(JObject parameters, string key)
        {
            if (parameters[key] is JArray array && array.Count > 0)
            {
                return array.Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            }

            return Array.Empty<string>();
        }
    }
}
