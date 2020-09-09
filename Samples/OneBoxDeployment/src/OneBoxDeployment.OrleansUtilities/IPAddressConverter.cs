using System;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneBoxDeployment.OrleansUtilities
{
    /// <summary>
	/// Converts <see cref="IPAddress"/> to and from strings.
	/// </summary>
	public class IPAddressConverter: JsonConverter<IPAddress>
    {
        /// <inheritdoc/>
        public override IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if(reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException();
            }

            return IPAddress.Parse(reader.GetString());
        }

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
