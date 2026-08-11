using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Orleans.Transactions.AdoNet.Utils;
internal static class JsonUtils
{
    public static byte[] SerializeWithNewtonsoftJson(object? value, JsonSerializerSettings jsonSettings)
    {
        var json = JsonConvert.SerializeObject(value, jsonSettings);
        return Encoding.UTF8.GetBytes(json);
    }

    public static T DeserializeWithNewtonsoftJson<T>(byte[] data, JsonSerializerSettings jsonSettings)
    {
        var json = Encoding.UTF8.GetString(data);
        return JsonConvert.DeserializeObject<T>(json, jsonSettings)
            ?? throw new JsonSerializationException($"Could not deserialize JSON as {typeof(T)}.");
    }
}
