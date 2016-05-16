using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Orleans.Runtime.Host
{
    internal class MembershipSerializerSettings : JsonSerializerSettings
    {
        public static readonly MembershipSerializerSettings Instance = new MembershipSerializerSettings();

        private MembershipSerializerSettings()
        {
            Converters.Add(new SiloAddressConverter());
            Converters.Add(new MembershipEntryConverter());
            Converters.Add(new StringEnumConverter());
        }

        private class MembershipEntryConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return (objectType == typeof(MembershipEntry));
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                MembershipEntry me = (MembershipEntry)value;
                writer.WriteStartObject();
                writer.WritePropertyName("SiloAddress"); serializer.Serialize(writer, me.SiloAddress);
                writer.WritePropertyName("HostName"); writer.WriteValue(me.HostName);
                writer.WritePropertyName("SiloName"); writer.WriteValue(me.SiloName);
                writer.WritePropertyName("InstanceName"); writer.WriteValue(me.SiloName);
                writer.WritePropertyName("Status"); serializer.Serialize(writer, me.Status);
                writer.WritePropertyName("ProxyPort"); writer.WriteValue(me.ProxyPort);
                writer.WritePropertyName("StartTime"); writer.WriteValue(me.StartTime);
                writer.WritePropertyName("SuspectTimes"); serializer.Serialize(writer, me.SuspectTimes);
                writer.WriteEndObject();
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
                JsonSerializer serializer)
            {
                JObject jo = JObject.Load(reader);
                return new MembershipEntry
                {
                    SiloAddress = jo["SiloAddress"].ToObject<SiloAddress>(serializer),
                    HostName = jo["HostName"].ToObject<string>(),
                    SiloName = (jo["SiloName"] ?? jo["InstanceName"]).ToObject<string>(),
                    Status = jo["Status"].ToObject<SiloStatus>(serializer),
                    ProxyPort = jo["ProxyPort"].Value<int>(),
                    StartTime = jo["StartTime"].Value<DateTime>(),
                    SuspectTimes = jo["SuspectTimes"].ToObject<List<Tuple<SiloAddress, DateTime>>>(serializer)
                };
            }
        }

        private class SiloAddressConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return (objectType == typeof(SiloAddress));
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                SiloAddress se = (SiloAddress)value;
                writer.WriteStartObject();
                writer.WritePropertyName("SiloAddress");
                writer.WriteValue(se.ToParsableString());
                writer.WriteEndObject();
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
                JsonSerializer serializer)
            {
                JObject jo = JObject.Load(reader);
                string seStr = jo["SiloAddress"].ToObject<string>(serializer);
                return SiloAddress.FromParsableString(seStr);
            }
        }
    }
}
