using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Azure.EventHubs;
using Orleans.Serialization;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Extends EventData to support streaming
    /// </summary>
    public static class EventDataExtensions
    {
        private const string EventDataPropertyStreamNamespaceKey = "StreamNamespace";

        /// <summary>
        /// Adds stream namespace to the EventData
        /// </summary>
        /// <param name="eventData"></param>
        /// <param name="streamNamespace"></param>
        public static void SetStreamNamespaceProperty(this EventData eventData, string streamNamespace)
        {
            eventData.Properties[EventDataPropertyStreamNamespaceKey] = streamNamespace;
        }

        /// <summary>
        /// Gets stream namespace from the EventData
        /// </summary>
        /// <param name="eventData"></param>
        /// <returns></returns>
        public static string GetStreamNamespaceProperty(this EventData eventData)
        {
            object namespaceObj;
            if (eventData.Properties.TryGetValue(EventDataPropertyStreamNamespaceKey, out namespaceObj))
            {
                return (string)namespaceObj;
            }
            return null;
        }

        /// <summary>
        /// Serializes event data properties
        /// </summary>
        /// <param name="eventData"></param>
        /// <param name="serializationManager"></param>
        /// <returns></returns>
        public static byte[] SerializeProperties(this EventData eventData, SerializationManager serializationManager)
        {
            var writeStream = new BinaryTokenStreamWriter();
            serializationManager.Serialize(eventData.Properties.Where(kvp => !string.Equals(kvp.Key, EventDataPropertyStreamNamespaceKey, StringComparison.Ordinal)).ToList(), writeStream);
            var result = writeStream.ToByteArray();
            writeStream.ReleaseBuffers();
            return result;
        }

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
