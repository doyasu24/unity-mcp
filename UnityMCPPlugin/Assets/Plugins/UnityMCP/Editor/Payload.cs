using System.Text.Json;

namespace UnityMcpPlugin
{
    internal static class Payload
    {
        internal static bool TryParseDocument(string raw, out JsonDocument document)
        {
            try
            {
                document = JsonDocument.Parse(raw);
                return true;
            }
            catch
            {
                document = null;
                return false;
            }
        }

        internal static string GetString(JsonElement map, string key)
        {
            if (map.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!map.TryGetProperty(key, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            return null;
        }

        internal static int? GetInt(JsonElement map, string key)
        {
            if (map.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!map.TryGetProperty(key, out var value))
            {
                return null;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    if (value.TryGetInt32(out var intValue))
                    {
                        return intValue;
                    }

                    if (value.TryGetInt64(out var longValue))
                    {
                        if (longValue < int.MinValue || longValue > int.MaxValue)
                        {
                            return null;
                        }

                        return (int)longValue;
                    }

                    if (value.TryGetDouble(out var doubleValue))
                    {
                        if (doubleValue < int.MinValue || doubleValue > int.MaxValue)
                        {
                            return null;
                        }

                        return (int)doubleValue;
                    }

                    return null;
                case JsonValueKind.String:
                {
                    var text = value.GetString();
                    if (int.TryParse(text, out var parsed))
                    {
                        return parsed;
                    }

                    return null;
                }
                default:
                    return null;
            }
        }

        internal static bool TryGetObject(JsonElement map, string key, out JsonElement value)
        {
            if (map.ValueKind != JsonValueKind.Object)
            {
                value = default;
                return false;
            }

            if (map.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            value = default;
            return false;
        }

        internal static JsonElement GetObjectOrEmpty(JsonElement map, string key)
        {
            return TryGetObject(map, key, out var value) ? value : default;
        }

        internal static bool TryGetArrayLength(JsonElement map, string key, out int length)
        {
            if (map.ValueKind != JsonValueKind.Object)
            {
                length = 0;
                return false;
            }

            if (map.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                length = value.GetArrayLength();
                return true;
            }

            length = 0;
            return false;
        }

        internal static string ToJson(JsonElement map)
        {
            return map.GetRawText();
        }
    }
}
