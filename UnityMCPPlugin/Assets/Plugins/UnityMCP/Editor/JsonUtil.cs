using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityMcpPlugin
{
    internal static class JsonUtil
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        internal static string Serialize(object obj)
        {
            return JsonSerializer.Serialize(obj, SerializerOptions);
        }
    }
}
