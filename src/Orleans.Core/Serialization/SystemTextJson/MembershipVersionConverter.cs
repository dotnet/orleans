#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    internal sealed class MembershipVersionConverter : JsonConverter<MembershipVersion>
    {
        public override MembershipVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new MembershipVersion(reader.GetInt64());

        public override void Write(Utf8JsonWriter writer, MembershipVersion value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
    }
}