using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    /// <summary>
    /// Unity ビルドパイプラインを管理するツール。
    /// build アクションは fire-and-forget パターン（RunTestsTool と同様）:
    /// - 初回呼び出し: ビルド開始、{ "status": "started" } を返す
    /// - 実行中の呼び出し: { "status": "building" } を返す
    /// - 完了後の呼び出し: キャッシュされたビルド結果を返す
    /// </summary>
    internal sealed class ManageBuildTool : AsyncToolHandler
    {
        private static readonly object _gate = new();
        private static bool _isBuilding;
        private static object _pendingResult;

        public override string ToolName => ToolNames.ManageBuild;

        /// <summary>ビルド中かどうか。GetEditorStateTool から参照される可能性がある。</summary>
        internal static bool IsBuilding
        {
            get { lock (_gate) return _isBuilding; }
        }

        public override Task<object> ExecuteAsync(JObject parameters)
        {
            // ポーリング: ビルド中または結果キャッシュがあればそちらを返す（RunTestsTool と同パターン）。
            // Server は空パラメータでポーリングするため、action バリデーションより先にチェックする。
            lock (_gate)
            {
                if (_isBuilding)
                {
                    return Task.FromResult<object>(new JObject { ["status"] = "building" });
                }

                if (_pendingResult != null)
                {
                    var result = _pendingResult;
                    _pendingResult = null;
                    return Task.FromResult(result);
                }
            }

            var action = Payload.GetString(parameters, "action");
            if (!ManageBuildActions.IsSupported(action))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"action must be one of: build, build_report, validate, get_platform, switch_platform, get_settings, set_settings, get_scenes, set_scenes, list_profiles, get_active_profile, set_active_profile");
            }

            // build アクション: async (fire-and-forget + ポーリング)
            if (action == ManageBuildActions.Build)
                return ExecuteBuildAsync(parameters);

            // 他のアクションは main thread で同期実行
            return MainThreadDispatcher.InvokeAsync(() => ExecuteSync(action, parameters));
        }

        // -----------------------------------------------------------
        // 同期アクションの dispatch
        // -----------------------------------------------------------

        private static object ExecuteSync(string action, JObject parameters)
        {
            // Profile アクションの Unity 6+ ガード
            if (ManageBuildActions.IsProfileAction(action))
            {
                return ExecuteProfileAction(action, parameters);
            }

            // mutation アクションの Play Mode ガード
            // (SetActiveProfile は ExecuteProfileAction 内で個別にガードする)
            if (action == ManageBuildActions.SwitchPlatform
                || action == ManageBuildActions.SetSettings
                || action == ManageBuildActions.SetScenes)
            {
                if (EditorApplication.isPlaying)
                {
                    throw new PluginException(SceneToolErrors.PlayModeActive,
                        "Cannot modify build settings while in Play Mode. Use control_play_mode to stop playback first.");
                }
            }

            switch (action)
            {
                case ManageBuildActions.GetPlatform:
                    return ExecuteGetPlatform();
                case ManageBuildActions.SwitchPlatform:
                    return ExecuteSwitchPlatform(parameters);
                case ManageBuildActions.GetSettings:
                    return ExecuteGetSettings(parameters);
                case ManageBuildActions.SetSettings:
                    return ExecuteSetSettings(parameters);
                case ManageBuildActions.GetScenes:
                    return ExecuteGetScenes();
                case ManageBuildActions.SetScenes:
                    return ExecuteSetScenes(parameters);
                case ManageBuildActions.Validate:
                    return ExecuteValidate();
                case ManageBuildActions.BuildReport:
                    return ExecuteBuildReport();
                default:
                    throw new PluginException("ERR_INVALID_PARAMS", $"Unknown action: {action}");
            }
        }

        // -----------------------------------------------------------
        // get_platform
        // -----------------------------------------------------------

        private static readonly Dictionary<BuildTarget, string> ReverseTargetMap = new()
        {
            [BuildTarget.StandaloneWindows64] = "windows64",
            [BuildTarget.StandaloneOSX] = "osx",
            [BuildTarget.StandaloneLinux64] = "linux64",
            [BuildTarget.Android] = "android",
            [BuildTarget.iOS] = "ios",
            [BuildTarget.WebGL] = "webgl",
        };

        private static readonly Dictionary<string, BuildTarget> TargetMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["windows64"] = BuildTarget.StandaloneWindows64,
                ["osx"] = BuildTarget.StandaloneOSX,
                ["linux64"] = BuildTarget.StandaloneLinux64,
                ["android"] = BuildTarget.Android,
                ["ios"] = BuildTarget.iOS,
                ["webgl"] = BuildTarget.WebGL,
            };

        private static readonly Dictionary<string, BuildOptions> OptionsMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["clean_build"] = BuildOptions.CleanBuildCache,
                ["auto_run"] = BuildOptions.AutoRunPlayer,
                ["show_built_player"] = BuildOptions.ShowBuiltPlayer,
                ["strict_mode"] = BuildOptions.StrictMode,
                ["detailed_build_report"] = BuildOptions.DetailedBuildReport,
            };

        private static object ExecuteGetPlatform()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var group = BuildPipeline.GetBuildTargetGroup(target);
            ReverseTargetMap.TryGetValue(target, out var friendlyName);

            return new JObject
            {
                ["action"] = "get_platform",
                ["build_target"] = target.ToString(),
                ["build_target_group"] = group.ToString(),
                ["friendly_name"] = friendlyName ?? target.ToString().ToLowerInvariant(),
            };
        }

        // -----------------------------------------------------------
        // switch_platform
        // -----------------------------------------------------------

        private static object ExecuteSwitchPlatform(JObject parameters)
        {
            var targetName = Payload.GetString(parameters, "target");
            if (string.IsNullOrEmpty(targetName) || !TargetMap.TryGetValue(targetName, out var buildTarget))
            {
                throw new PluginException(BuildToolErrors.InvalidTarget,
                    $"Unknown build target: {targetName}");
            }

            var group = BuildPipeline.GetBuildTargetGroup(buildTarget);
            var success = EditorUserBuildSettings.SwitchActiveBuildTarget(group, buildTarget);
            if (!success)
            {
                throw new PluginException(BuildToolErrors.PlatformSwitchFailed,
                    $"Failed to switch to platform: {targetName}");
            }

            ReverseTargetMap.TryGetValue(buildTarget, out var friendlyName);
            return new JObject
            {
                ["action"] = "switch_platform",
                ["build_target"] = buildTarget.ToString(),
                ["build_target_group"] = group.ToString(),
                ["friendly_name"] = friendlyName ?? targetName,
            };
        }

        // -----------------------------------------------------------
        // get_settings / set_settings
        // -----------------------------------------------------------

        private static object ExecuteGetSettings(JObject parameters)
        {
            var property = Payload.GetString(parameters, "property");
            if (string.IsNullOrEmpty(property) || !BuildSettingsProperties.IsSupported(property))
            {
                throw new PluginException(BuildToolErrors.InvalidProperty,
                    $"Unknown property: {property}");
            }

            var value = ReadPlayerSetting(property);
            return new JObject
            {
                ["action"] = "get_settings",
                ["property"] = property,
                ["value"] = value,
            };
        }

        private static object ExecuteSetSettings(JObject parameters)
        {
            var property = Payload.GetString(parameters, "property");
            if (string.IsNullOrEmpty(property) || !BuildSettingsProperties.IsSupported(property))
            {
                throw new PluginException(BuildToolErrors.InvalidProperty,
                    $"Unknown property: {property}");
            }

            var value = Payload.GetString(parameters, "value") ?? string.Empty;
            var definesAction = Payload.GetString(parameters, "defines_action") ?? DefinesActions.Set;

            WritePlayerSetting(property, value, definesAction);

            // 書き込み後の値を読み戻して返す
            var readBack = ReadPlayerSetting(property);
            return new JObject
            {
                ["action"] = "set_settings",
                ["property"] = property,
                ["value"] = readBack,
            };
        }

        private static string ReadPlayerSetting(string property)
        {
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            switch (property)
            {
                case BuildSettingsProperties.ProductName:
                    return PlayerSettings.productName;
                case BuildSettingsProperties.CompanyName:
                    return PlayerSettings.companyName;
                case BuildSettingsProperties.Version:
                    return PlayerSettings.bundleVersion;
                case BuildSettingsProperties.BundleId:
                    return PlayerSettings.applicationIdentifier;
                case BuildSettingsProperties.ScriptingBackend:
                    return PlayerSettings.GetScriptingBackend(group).ToString();
                case BuildSettingsProperties.Defines:
#if UNITY_6000_0_OR_NEWER
                    return PlayerSettings.GetScriptingDefineSymbols(
                        UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group));
#else
                    return PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
#endif
                default:
                    throw new PluginException(BuildToolErrors.InvalidProperty,
                        $"Unknown property: {property}");
            }
        }

        private static void WritePlayerSetting(string property, string value, string definesAction)
        {
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            switch (property)
            {
                case BuildSettingsProperties.ProductName:
                    PlayerSettings.productName = value;
                    break;
                case BuildSettingsProperties.CompanyName:
                    PlayerSettings.companyName = value;
                    break;
                case BuildSettingsProperties.Version:
                    PlayerSettings.bundleVersion = value;
                    break;
                case BuildSettingsProperties.BundleId:
                    PlayerSettings.applicationIdentifier = value;
                    break;
                case BuildSettingsProperties.ScriptingBackend:
                    if (Enum.TryParse<ScriptingImplementation>(value, true, out var backend))
                    {
                        PlayerSettings.SetScriptingBackend(group, backend);
                    }
                    else
                    {
                        throw new PluginException("ERR_INVALID_PARAMS",
                            $"Invalid scripting backend: {value}. Use 'Mono2x' or 'IL2CPP'.");
                    }

                    break;
                case BuildSettingsProperties.Defines:
                    ApplyDefines(group, value, definesAction);
                    break;
                default:
                    throw new PluginException(BuildToolErrors.InvalidProperty,
                        $"Unknown property: {property}");
            }
        }

        private static void ApplyDefines(BuildTargetGroup group, string value, string definesAction)
        {
#if UNITY_6000_0_OR_NEWER
            var namedTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group);
            var current = PlayerSettings.GetScriptingDefineSymbols(namedTarget);
#else
            var current = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
#endif
            string newDefines;
            switch (definesAction)
            {
                case DefinesActions.Add:
                {
                    var existing = new HashSet<string>(
                        current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
                    foreach (var sym in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        existing.Add(sym.Trim());
                    newDefines = string.Join(";", existing);
                    break;
                }
                case DefinesActions.Remove:
                {
                    var existing = new HashSet<string>(
                        current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
                    foreach (var sym in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        existing.Remove(sym.Trim());
                    newDefines = string.Join(";", existing);
                    break;
                }
                default: // DefinesActions.Set
                    newDefines = value;
                    break;
            }

#if UNITY_6000_0_OR_NEWER
            PlayerSettings.SetScriptingDefineSymbols(namedTarget, newDefines);
#else
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, newDefines);
#endif
        }

        // -----------------------------------------------------------
        // get_scenes / set_scenes
        // -----------------------------------------------------------

        private static object ExecuteGetScenes()
        {
            var scenes = EditorBuildSettings.scenes;
            var arr = new JArray();
            foreach (var s in scenes)
            {
                arr.Add(new JObject
                {
                    ["path"] = s.path,
                    ["enabled"] = s.enabled,
                });
            }

            return new JObject
            {
                ["action"] = "get_scenes",
                ["scenes"] = arr,
            };
        }

        private static object ExecuteSetScenes(JObject parameters)
        {
            var buildScenes = parameters["build_scenes"] as JArray;
            if (buildScenes == null || buildScenes.Count == 0)
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    "build_scenes is required for 'set_scenes' action");
            }

            var sceneList = new List<EditorBuildSettingsScene>();
            foreach (var item in buildScenes)
            {
                var path = Payload.GetString(item, "path");
                if (string.IsNullOrEmpty(path)) continue;

                var enabled = Payload.GetBool(item, "enabled") ?? true;
                sceneList.Add(new EditorBuildSettingsScene(path, enabled));
            }

            EditorBuildSettings.scenes = sceneList.ToArray();

            // 書き込み後の状態を返す
            return ExecuteGetScenes();
        }

        // -----------------------------------------------------------
        // validate（独自機能: ビルド前プリフライトチェック）
        // -----------------------------------------------------------

        private static object ExecuteValidate()
        {
            var issues = new JArray();
            int errorCount = 0;
            int warningCount = 0;

            // 1. シーン存在チェック
            var scenes = EditorBuildSettings.scenes;
            foreach (var scene in scenes)
            {
                if (!scene.enabled) continue;
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scene.path);
                if (asset == null)
                {
                    issues.Add(new JObject
                    {
                        ["severity"] = "error",
                        ["category"] = "scene",
                        ["message"] = $"Scene not found: {scene.path}",
                    });
                    errorCount++;
                }
            }

            // 2. コンパイルエラーチェック
            if (EditorUtility.scriptCompilationFailed)
            {
                issues.Add(new JObject
                {
                    ["severity"] = "error",
                    ["category"] = "compilation",
                    ["message"] = "Script compilation errors detected",
                });
                errorCount++;
            }

            // 3. 欠損スクリプト検出（開いているシーンのみ）
            var allGameObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            int missingScriptCount = 0;
            foreach (var go in allGameObjects)
            {
                // シーン上のオブジェクトのみ（プレハブアセット等を除外）
                if (!go.scene.isLoaded) continue;
                missingScriptCount += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
            }

            if (missingScriptCount > 0)
            {
                issues.Add(new JObject
                {
                    ["severity"] = "warning",
                    ["category"] = "missing_script",
                    ["message"] = $"{missingScriptCount} GameObjects with missing scripts in loaded scenes",
                });
                warningCount++;
            }

            return new JObject
            {
                ["action"] = "validate",
                ["valid"] = errorCount == 0,
                ["issues"] = issues,
                ["summary"] = new JObject
                {
                    ["errors"] = errorCount,
                    ["warnings"] = warningCount,
                },
            };
        }

        // -----------------------------------------------------------
        // build_report（独自機能: ビルドレポート取得）
        // -----------------------------------------------------------

        private static object ExecuteBuildReport()
        {
            // Library/LastBuild.buildreport は Unity の内部パス
            var report = AssetDatabase.LoadAssetAtPath<BuildReport>("Library/LastBuild.buildreport");
            if (report == null)
            {
                return new JObject
                {
                    ["action"] = "build_report",
                    ["available"] = false,
                };
            }

            var summary = report.summary;
            ReverseTargetMap.TryGetValue(summary.platform, out var friendlyPlatform);

            var result = new JObject
            {
                ["action"] = "build_report",
                ["available"] = true,
                ["summary"] = new JObject
                {
                    ["result"] = summary.result.ToString(),
                    ["platform"] = friendlyPlatform ?? summary.platform.ToString(),
                    ["total_size_bytes"] = (long)summary.totalSize,
                    ["total_errors"] = summary.totalErrors,
                    ["total_warnings"] = summary.totalWarnings,
                    ["duration_ms"] = (long)(summary.totalTime.TotalMilliseconds),
                },
            };

            // ビルドステップ
            var stepsArray = new JArray();
            foreach (var step in report.steps)
            {
                stepsArray.Add(new JObject
                {
                    ["name"] = step.name,
                    ["duration_ms"] = (long)(step.duration.TotalMilliseconds),
                });
            }

            result["steps"] = stepsArray;

            // PackedAssets によるサイズ内訳
            var sizeBreakdown = new Dictionary<string, long>();
            var largestAssets = new List<(string path, long size)>();

            if (report.packedAssets != null)
            {
                foreach (var packed in report.packedAssets)
                {
                    if (packed.contents == null) continue;
                    foreach (var entry in packed.contents)
                    {
                        var category = entry.type?.ToString() ?? "Other";
                        if (!sizeBreakdown.ContainsKey(category))
                            sizeBreakdown[category] = 0;
                        sizeBreakdown[category] += (long)entry.packedSize;

                        // ソースアセットパスがある場合に最大ファイル候補に追加
                        if (!string.IsNullOrEmpty(entry.sourceAssetPath))
                        {
                            largestAssets.Add((entry.sourceAssetPath, (long)entry.packedSize));
                        }
                    }
                }
            }

            var breakdownArray = new JArray();
            foreach (var kvp in sizeBreakdown.OrderByDescending(k => k.Value))
            {
                breakdownArray.Add(new JObject
                {
                    ["category"] = kvp.Key,
                    ["size_bytes"] = kvp.Value,
                });
            }

            result["size_breakdown"] = breakdownArray;

            // 最大ファイル上位10件
            var topAssets = new JArray();
            foreach (var asset in largestAssets.OrderByDescending(a => a.size).Take(10))
            {
                topAssets.Add(new JObject
                {
                    ["path"] = asset.path,
                    ["size_bytes"] = asset.size,
                });
            }

            result["largest_assets"] = topAssets;

            return result;
        }

        // -----------------------------------------------------------
        // build（async fire-and-forget パターン）
        // -----------------------------------------------------------

        private Task<object> ExecuteBuildAsync(JObject parameters)
        {
            // ポーリングチェックは ExecuteAsync 冒頭で実施済み。
            // ここに到達するのは新規ビルド開始時のみ。

            // Play Mode ガード（main thread で確認）
            if (EditorApplication.isPlaying)
            {
                throw new PluginException(SceneToolErrors.PlayModeActive,
                    "Cannot build while in Play Mode. Use control_play_mode to stop playback first.");
            }

            // パラメータ解析
            var targetName = Payload.GetString(parameters, "target");
            var outputPath = Payload.GetString(parameters, "output_path");
            if (string.IsNullOrEmpty(outputPath))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "output_path is required for 'build' action");
            }

            BuildTarget buildTarget;
            if (!string.IsNullOrEmpty(targetName))
            {
                if (!TargetMap.TryGetValue(targetName, out buildTarget))
                {
                    throw new PluginException(BuildToolErrors.InvalidTarget, $"Unknown build target: {targetName}");
                }
            }
            else
            {
                buildTarget = EditorUserBuildSettings.activeBuildTarget;
            }

            // シーン
            string[] scenes = null;
            var scenesToken = parameters["scenes"] as JArray;
            if (scenesToken != null && scenesToken.Count > 0)
            {
                scenes = scenesToken.Select(s => s.Value<string>()).Where(s => s != null).ToArray();
            }

            if (scenes == null || scenes.Length == 0)
            {
                scenes = EditorBuildSettings.scenes
                    .Where(s => s.enabled)
                    .Select(s => s.path)
                    .ToArray();
            }

            // BuildOptions
            var buildOptions = BuildOptions.None;
            var development = Payload.GetBool(parameters, "development") ?? false;
            if (development)
            {
                buildOptions |= BuildOptions.Development;
            }

            var optionsToken = parameters["options"] as JArray;
            if (optionsToken != null)
            {
                foreach (var opt in optionsToken)
                {
                    var optStr = opt.Value<string>();
                    if (optStr != null && OptionsMap.TryGetValue(optStr, out var flag))
                    {
                        buildOptions |= flag;
                    }
                }
            }

            // subtarget
            var subtarget = Payload.GetString(parameters, "subtarget");

            // ビルド開始
            lock (_gate)
            {
                _isBuilding = true;
                _pendingResult = null;
            }

            _ = ExecuteAndCacheBuildResultAsync(buildTarget, outputPath, scenes, buildOptions, subtarget);

            return Task.FromResult<object>(new JObject { ["status"] = "started" });
        }

        /// <summary>
        /// メインスレッドでビルドを実行し、結果をキャッシュに格納する。
        /// </summary>
        private static async Task ExecuteAndCacheBuildResultAsync(
            BuildTarget buildTarget, string outputPath, string[] scenes,
            BuildOptions buildOptions, string subtarget)
        {
            try
            {
                var result = await MainThreadDispatcher.InvokeAsync(() =>
                {
                    var buildPlayerOptions = new BuildPlayerOptions
                    {
                        scenes = scenes,
                        locationPathName = outputPath,
                        target = buildTarget,
                        options = buildOptions,
                    };

                    // subtarget の設定
                    if (!string.IsNullOrEmpty(subtarget)
                        && string.Equals(subtarget, "server", StringComparison.OrdinalIgnoreCase))
                    {
#if UNITY_2021_2_OR_NEWER
                        buildPlayerOptions.subtarget = (int)StandaloneBuildSubtarget.Server;
#endif
                    }

                    var report = BuildPipeline.BuildPlayer(buildPlayerOptions);
                    return BuildResultFromReport(report, buildTarget, outputPath);
                });

                lock (_gate)
                {
                    _isBuilding = false;
                    _pendingResult = result;
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _isBuilding = false;
                    _pendingResult = new JObject
                    {
                        ["action"] = "build",
                        ["result"] = "Failed",
                        ["error"] = ex.Message,
                    };
                }
            }
        }

        private static object BuildResultFromReport(BuildReport report, BuildTarget buildTarget, string outputPath)
        {
            ReverseTargetMap.TryGetValue(buildTarget, out var friendlyName);
            var summary = report.summary;

            var result = new JObject
            {
                ["action"] = "build",
                ["result"] = summary.result.ToString(),
                ["target"] = friendlyName ?? buildTarget.ToString(),
                ["output_path"] = outputPath,
                ["duration_ms"] = (long)(summary.totalTime.TotalMilliseconds),
                ["total_size_bytes"] = (long)summary.totalSize,
                ["total_errors"] = summary.totalErrors,
                ["total_warnings"] = summary.totalWarnings,
            };

            // ビルドステップ
            var stepsArray = new JArray();
            foreach (var step in report.steps)
            {
                stepsArray.Add(new JObject
                {
                    ["name"] = step.name,
                    ["duration_ms"] = (long)(step.duration.TotalMilliseconds),
                });
            }

            result["steps"] = stepsArray;

            // エラー・警告メッセージ
            var errors = new JArray();
            var warnings = new JArray();
            foreach (var step in report.steps)
            {
                foreach (var msg in step.messages)
                {
                    if (msg.type == LogType.Error || msg.type == LogType.Exception)
                        errors.Add(msg.content);
                    else if (msg.type == LogType.Warning)
                        warnings.Add(msg.content);
                }
            }

            if (errors.Count > 0) result["errors"] = errors;
            if (warnings.Count > 0) result["warnings"] = warnings;

            return result;
        }

        // -----------------------------------------------------------
        // Build Profiles（Unity 6+ のみ）
        // -----------------------------------------------------------

        private static object ExecuteProfileAction(string action, JObject parameters)
        {
#if UNITY_6000_0_OR_NEWER
            switch (action)
            {
                case ManageBuildActions.ListProfiles:
                    return ExecuteListProfiles();
                case ManageBuildActions.GetActiveProfile:
                    return ExecuteGetActiveProfile();
                case ManageBuildActions.SetActiveProfile:
                    return ExecuteSetActiveProfile(parameters);
                default:
                    throw new PluginException("ERR_INVALID_PARAMS", $"Unknown profile action: {action}");
            }
#else
            throw new PluginException(BuildToolErrors.ProfilesNotSupported,
                "Build profiles require Unity 6 or newer");
#endif
        }

#if UNITY_6000_0_OR_NEWER
        private static object ExecuteListProfiles()
        {
            var guids = AssetDatabase.FindAssets("t:BuildProfile");
            var activeProfile = UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile();
            var profiles = new JArray();

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var profile = AssetDatabase.LoadAssetAtPath<UnityEditor.Build.Profile.BuildProfile>(path);
                if (profile == null) continue;

                profiles.Add(new JObject
                {
                    ["name"] = profile.name,
                    ["path"] = path,
                    ["is_active"] = ReferenceEquals(profile, activeProfile),
                });
            }

            return new JObject
            {
                ["action"] = "list_profiles",
                ["profiles"] = profiles,
            };
        }

        private static object ExecuteGetActiveProfile()
        {
            var profile = UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile();
            if (profile == null)
            {
                return new JObject
                {
                    ["action"] = "get_active_profile",
                    ["has_active_profile"] = false,
                };
            }

            var path = AssetDatabase.GetAssetPath(profile);
            var scenes = new JArray();
            foreach (var scene in profile.scenes)
            {
                scenes.Add(new JObject
                {
                    ["path"] = scene.path,
                    ["enabled"] = scene.enabled,
                });
            }

            return new JObject
            {
                ["action"] = "get_active_profile",
                ["has_active_profile"] = true,
                ["name"] = profile.name,
                ["path"] = path,
                ["scenes"] = scenes,
            };
        }

        private static object ExecuteSetActiveProfile(JObject parameters)
        {
            if (EditorApplication.isPlaying)
            {
                throw new PluginException(SceneToolErrors.PlayModeActive,
                    "Cannot change active build profile while in Play Mode.");
            }

            var profilePath = Payload.GetString(parameters, "profile_path");
            if (string.IsNullOrEmpty(profilePath))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    "profile_path is required for 'set_active_profile' action");
            }

            if (string.Equals(profilePath, "none", StringComparison.OrdinalIgnoreCase))
            {
                UnityEditor.Build.Profile.BuildProfile.SetActiveBuildProfile(null);
                return new JObject
                {
                    ["action"] = "set_active_profile",
                    ["name"] = "",
                    ["path"] = "",
                };
            }

            var profile = AssetDatabase.LoadAssetAtPath<UnityEditor.Build.Profile.BuildProfile>(profilePath);
            if (profile == null)
            {
                throw new PluginException(BuildToolErrors.ProfileNotFound,
                    $"Build profile not found at: {profilePath}");
            }

            UnityEditor.Build.Profile.BuildProfile.SetActiveBuildProfile(profile);
            return new JObject
            {
                ["action"] = "set_active_profile",
                ["name"] = profile.name,
                ["path"] = profilePath,
            };
        }
#endif
    }
}
