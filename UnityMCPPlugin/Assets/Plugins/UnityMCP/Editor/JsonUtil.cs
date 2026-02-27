using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityMcpPlugin
{
    internal static class JsonUtil
    {
        private static readonly JsonSerializerSettings SerializerOptions = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
        };

        internal static string Serialize(object obj)
        {
            return JsonConvert.SerializeObject(obj, SerializerOptions);
        }

        internal static JToken SerializeToToken(object obj)
        {
            if (obj == null)
            {
                return JValue.CreateNull();
            }

            var serializer = JsonSerializer.Create(SerializerOptions);
            return JToken.FromObject(obj, serializer);
        }
    }
}
