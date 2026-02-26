using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace UnityMcpServer;

internal static class JsonRpc
{
    public static JsonObject Result(JsonNode? id, JsonNode result)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result.DeepClone(),
        };
    }

    public static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["data"] = data?.DeepClone(),
            },
        };

        if (data is null)
        {
            var error = payload["error"] as JsonObject;
            error?.Remove("data");
        }

        return payload;
    }

    public static Task WriteAsync(HttpContext context, JsonNode node)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(node.ToJsonString(JsonDefaults.Options));
    }
}

internal static class JsonHelpers
{
    public static string? GetString(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }

    public static int? GetInt(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var number))
        {
            return number;
        }

        return null;
    }

    public static ulong? GetUlong(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<ulong>(out var ulongValue))
            {
                return ulongValue;
            }

            if (jsonValue.TryGetValue<long>(out var longValue) && longValue >= 0)
            {
                return (ulong)longValue;
            }

            if (jsonValue.TryGetValue<int>(out var intValue) && intValue >= 0)
            {
                return (ulong)intValue;
            }
        }

        return null;
    }

    public static JsonObject AsObjectOrEmpty(JsonNode? node)
    {
        return node as JsonObject ?? new JsonObject();
    }

    public static JsonNode? CloneNode(JsonNode? node)
    {
        return node?.DeepClone();
    }
}
