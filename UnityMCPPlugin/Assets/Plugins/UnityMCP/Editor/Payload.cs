using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityMcpPlugin
{
    internal static class Payload
    {
        internal static bool TryParseDocument(string raw, out JObject document)
        {
            try
            {
                document = JObject.Parse(raw);
                return true;
            }
            catch
            {
                document = null;
                return false;
            }
        }

        internal static string GetString(JToken map, string key)
        {
            if (map?.Type != JTokenType.Object)
            {
                return null;
            }

            var value = map[key];
            if (value == null)
            {
                return null;
            }

            if (value.Type == JTokenType.String)
            {
                return value.Value<string>();
            }

            return null;
        }

        internal static int? GetInt(JToken map, string key)
        {
            if (map?.Type != JTokenType.Object)
            {
                return null;
            }

            var value = map[key];
            if (value == null)
            {
                return null;
            }

            switch (value.Type)
            {
                case JTokenType.Integer:
                {
                    var longValue = value.Value<long>();
                    if (longValue >= int.MinValue && longValue <= int.MaxValue)
                    {
                        return (int)longValue;
                    }

                    return null;
                }
                case JTokenType.Float:
                {
                    var doubleValue = value.Value<double>();
                    if (doubleValue >= int.MinValue && doubleValue <= int.MaxValue)
                    {
                        return (int)doubleValue;
                    }

                    return null;
                }
                case JTokenType.String:
                {
                    var text = value.Value<string>();
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

        internal static bool TryGetObject(JToken map, string key, out JObject value)
        {
            if (map?.Type != JTokenType.Object)
            {
                value = null;
                return false;
            }

            if (map[key] is JObject objectValue)
            {
                value = objectValue;
                return true;
            }

            value = null;
            return false;
        }

        internal static JObject GetObjectOrEmpty(JToken map, string key)
        {
            return TryGetObject(map, key, out var value) ? value : new JObject();
        }

        internal static bool TryGetArrayLength(JToken map, string key, out int length)
        {
            if (map?.Type != JTokenType.Object)
            {
                length = 0;
                return false;
            }

            if (map[key] is JArray arrayValue)
            {
                length = arrayValue.Count;
                return true;
            }

            length = 0;
            return false;
        }

        internal static string ToJson(JToken map)
        {
            return map == null ? "null" : map.ToString(Formatting.None);
        }
    }
}
