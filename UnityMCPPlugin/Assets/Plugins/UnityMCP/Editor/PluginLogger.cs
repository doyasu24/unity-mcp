using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityMcpPlugin
{
    internal static class PluginLogger
    {
        internal static void Info(string message, params (string Key, object Value)[] context)
        {
            LogInternal(LogType.Log, message, context);
        }

        internal static void Warn(string message, params (string Key, object Value)[] context)
        {
            LogInternal(LogType.Warning, message, context);
        }

        internal static void Error(string message, params (string Key, object Value)[] context)
        {
            LogInternal(LogType.Error, message, context);
        }

        private static void LogInternal(LogType type, string message, params (string Key, object Value)[] context)
        {
            var payload = new JsonObject
            {
                ["level"] = type == LogType.Error ? "ERROR" : type == LogType.Warning ? "WARN" : "INFO",
                ["ts"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["msg"] = message,
            };

            for (var i = 0; i < context.Length; i += 1)
            {
                payload[context[i].Key] = ToJsonNode(context[i].Value);
            }

            var text = JsonUtil.Serialize(payload);
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

        private static JsonNode ToJsonNode(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is JsonNode jsonNode)
            {
                return jsonNode.DeepClone();
            }

            if (value is Exception ex)
            {
                return new JsonObject
                {
                    ["type"] = ex.GetType().Name,
                    ["message"] = ex.Message,
                };
            }

            return JsonSerializer.SerializeToNode(value);
        }
    }
}
