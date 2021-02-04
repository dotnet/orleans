using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain storage serializer that uses Newtonsoft.Json
    /// </summary>
    public class JsonGrainStorageSerializer : IGrainStorageSerializer
    {
        private static List<string> supportedTags => new List<string> { WellKnownSerializerTag.Json };

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public List<string> SupportedTags => supportedTags;

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public string Serialize(Type t, object value, out BinaryData output)
        {
            var data = JsonConvert.SerializeObject(value);
            output = new BinaryData(data);
            return WellKnownSerializerTag.Json;
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public object Deserialize(Type expected, BinaryData input, string tag)
        {
            if (!tag.Equals(WellKnownSerializerTag.Json, StringComparison.InvariantCultureIgnoreCase))
            {
                throw new ArgumentException($"Unsupported tag '{tag}'", nameof(tag));
            }

            return JsonConvert.DeserializeObject(Encoding.UTF8.GetString(input.ToArray()));
        }
    }
}
