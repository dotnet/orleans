using System;
using Newtonsoft.Json;
using System.Net;
using Orleans.Runtime;
using Newtonsoft.Json.Linq;
using System.Globalization;

namespace Orleans.Clustering.Redis
{
    internal static class JsonSettings
    {
        public static JsonSerializerSettings JsonSerializerSettings => new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            TypeNameHandling = TypeNameHandling.None,
            DefaultValueHandling = DefaultValueHandling.Include,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            DateParseHandling = DateParseHandling.DateTimeOffset,
            Culture = CultureInfo.InvariantCulture,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            PreserveReferencesHandling = PreserveReferencesHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 10,
            Converters = { new IPAddressConverter(), new IPEndPointConverter(), new SiloAddressConverter() }
        };

        private class IPAddressConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(IPAddress);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                IPAddress ip = (IPAddress)value;
                writer.WriteValue(ip.ToString());
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                JToken token = JToken.Load(reader);
                return IPAddress.Parse(token.Value<string>());
            }
        }

        private class IPEndPointConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(IPEndPoint);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                IPEndPoint ep = (IPEndPoint)value;
                writer.WriteStartObject();
                writer.WritePropertyName("Address");
                serializer.Serialize(writer, ep.Address);
                writer.WritePropertyName("Port");
                writer.WriteValue(ep.Port);
                writer.WriteEndObject();
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                JObject jo = JObject.Load(reader);
                IPAddress address = jo["Address"].ToObject<IPAddress>(serializer);
                int port = jo["Port"].Value<int>();
                return new IPEndPoint(address, port);
            }
        }

        private class SiloAddressConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(SiloAddress);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                SiloAddress addr = (SiloAddress)value;
                writer.WriteStartObject();
                writer.WritePropertyName("SiloAddress");
                writer.WriteValue(addr.ToParsableString());
                writer.WriteEndObject();
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                JObject jo = JObject.Load(reader);
                SiloAddress addr = SiloAddress.FromParsableString(jo["SiloAddress"].ToObject<string>());
                return addr;
            }
        }
    }
}