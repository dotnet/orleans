using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    internal class UniqueKeyConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(UniqueKey));
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            UniqueKey key = (UniqueKey)value;
            writer.WriteStartObject();
            writer.WritePropertyName("UniqueKey");
            writer.WriteValue(key.ToHexString());
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            UniqueKey addr = UniqueKey.Parse(jo["UniqueKey"].ToObject<string>());
            return addr;
        }
    }
}