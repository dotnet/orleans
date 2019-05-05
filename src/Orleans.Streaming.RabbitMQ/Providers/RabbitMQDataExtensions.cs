using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Serialization;

namespace Orleans.RabbitMQ.Providers
{
    public static class RabbitMQDataExtensions
    {
        /// <summary>
        /// Deserializes event data properties
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="serializationManager"></param>
        /// <returns></returns>
        public static IDictionary<string, object> DeserializeProperties(this ArraySegment<byte> bytes, SerializationManager serializationManager)
        {
            var stream = new BinaryTokenStreamReader(bytes);
            return serializationManager.Deserialize<List<KeyValuePair<string, object>>>(stream).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }
}
