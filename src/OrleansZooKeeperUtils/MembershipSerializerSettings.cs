/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
                writer.WritePropertyName("InstanceName"); writer.WriteValue(me.InstanceName);
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
                    IsPrimary = true,
                    SiloAddress = jo["SiloAddress"].ToObject<SiloAddress>(serializer),
                    HostName = jo["HostName"].ToObject<string>(),
                    InstanceName = jo["InstanceName"].ToObject<string>(),
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