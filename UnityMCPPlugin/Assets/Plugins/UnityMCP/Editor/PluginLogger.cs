using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin
{
    internal static class PluginLogger
    {
        private const string Prefix = "[Unity MCP]";
        private const string DevPrefix = "[Unity MCP][dev]";
        private const string DetailedLogsEditorPrefKeyPrefix = "UnityMCPPlugin.DetailedLogs";
        private static readonly object DetailedLogsGate = new();
        private static bool _detailedLogsEnabledCache;
        private static bool _detailedLogsCacheLoaded;
        private static int _mainThreadId = -1;

        internal static void InitializeMainThreadState()
        {
            lock (DetailedLogsGate)
            {
                if (_mainThreadId < 0)
                {
                    _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                }
            }

            EnsureDetailedLogsCacheLoadedOnMainThread();
        }

        internal static bool GetDetailedLogsEnabled()
        {
            EnsureDetailedLogsCacheLoadedOnMainThread();

            lock (DetailedLogsGate)
            {
                return _detailedLogsEnabledCache;
            }
        }

        internal static void SetDetailedLogsEnabled(bool enabled)
        {
            lock (DetailedLogsGate)
            {
                _detailedLogsEnabledCache = enabled;
                _detailedLogsCacheLoaded = true;
            }

            if (IsMainThread())
            {
                EditorPrefs.SetBool(GetDetailedLogsEditorPrefKey(), enabled);
            }
        }

        internal static bool IsPluginLog(string condition)
        {
            if (string.IsNullOrEmpty(condition))
            {
                return false;
            }

            return condition.StartsWith(Prefix, StringComparison.Ordinal);
        }

        internal static void UserInfo(string message, params (string Key, object Value)[] context)
        {
            LogUserInternal(LogType.Log, message, context);
        }

        internal static void UserWarn(string message, params (string Key, object Value)[] context)
        {
            LogUserInternal(LogType.Warning, message, context);
        }

        internal static void UserError(string message, params (string Key, object Value)[] context)
        {
            LogUserInternal(LogType.Error, message, context);
        }

        internal static void DevInfo(string message, params (string Key, object Value)[] context)
        {
            LogDeveloperInternal(LogType.Log, message, context);
        }

        internal static void DevWarn(string message, params (string Key, object Value)[] context)
        {
            LogDeveloperInternal(LogType.Warning, message, context);
        }

        internal static void DevError(string message, params (string Key, object Value)[] context)
        {
            LogDeveloperInternal(LogType.Error, message, context);
        }

        internal static void Info(string message, params (string Key, object Value)[] context)
        {
            UserInfo(message, context);
        }

        internal static void Warn(string message, params (string Key, object Value)[] context)
        {
            UserWarn(message, context);
        }

        internal static void Error(string message, params (string Key, object Value)[] context)
        {
            UserError(message, context);
        }

        private static void LogUserInternal(LogType type, string message, params (string Key, object Value)[] context)
        {
            var text = BuildUserText(message, context);
            WriteUnityLog(type, text);
        }

        private static void LogDeveloperInternal(LogType type, string message, params (string Key, object Value)[] context)
        {
            if (!GetDetailedLogsEnabled())
            {
                return;
            }

            var text = $"{DevPrefix} {BuildStructuredText(type, message, context)}";
            WriteUnityLog(type, text);
        }

        private static string BuildUserText(string message, IReadOnlyList<(string Key, object Value)> context)
        {
            if (context.Count == 0)
            {
                return $"{Prefix} {message}";
            }

            var builder = new StringBuilder();
            builder.Append(Prefix);
            builder.Append(' ');
            builder.Append(message);
            builder.Append(" (");

            for (var i = 0; i < context.Count; i += 1)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(context[i].Key);
                builder.Append('=');
                builder.Append(ToUserValue(context[i].Value));
            }

            builder.Append(')');
            return builder.ToString();
        }

        private static string BuildStructuredText(LogType type, string message, IReadOnlyList<(string Key, object Value)> context)
        {
            var payload = new JObject
            {
                ["level"] = type == LogType.Error ? "ERROR" : type == LogType.Warning ? "WARN" : "INFO",
                ["ts"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["msg"] = message,
            };

            for (var i = 0; i < context.Count; i += 1)
            {
                payload[context[i].Key] = ToJsonToken(context[i].Value);
            }

            return JsonUtil.Serialize(payload);
        }

        private static void WriteUnityLog(LogType type, string text)
        {
            switch (type)
            {
                case LogType.Error:
                    Debug.LogError(text);
                    break;
                case LogType.Warning:
                    Debug.LogWarning(text);
                    break;
                default:
                    Debug.Log(text);
                    break;
            }
        }

        private static string ToUserValue(object value)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is Exception ex)
            {
                return ex.Message;
            }

            if (value is JToken jsonToken)
            {
                return JsonUtil.Serialize(jsonToken);
            }

            return value.ToString();
        }

        private static JToken ToJsonToken(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is JToken jsonToken)
            {
                return jsonToken.DeepClone();
            }

            if (value is Exception ex)
            {
                return new JObject
                {
                    ["type"] = ex.GetType().Name,
                    ["message"] = ex.Message,
                };
            }

            return JsonUtil.SerializeToToken(value);
        }

        private static string GetDetailedLogsEditorPrefKey()
        {
            return $"{DetailedLogsEditorPrefKeyPrefix}:{Application.dataPath}";
        }

        private static void EnsureDetailedLogsCacheLoadedOnMainThread()
        {
            if (!IsMainThread())
            {
                return;
            }

            lock (DetailedLogsGate)
            {
                if (_detailedLogsCacheLoaded)
                {
                    return;
                }

                _detailedLogsEnabledCache = EditorPrefs.GetBool(GetDetailedLogsEditorPrefKey(), false);
                _detailedLogsCacheLoaded = true;
            }
        }

        private static bool IsMainThread()
        {
            lock (DetailedLogsGate)
            {
                return _mainThreadId >= 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;
            }
        }
    }
}
