
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ServiceBus.Messaging;
using Orleans.Serialization;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Extends EventData to support streaming
    /// </summary>
    public static class EventDataExtensions
    {
        private const string EventDataPropertyStreamNamespaceKey = "StreamNamespace";
        private static readonly string[] SkipProperties = { EventDataPropertyStreamNamespaceKey };

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
        /// <returns></returns>
        public static byte[] SerializeProperties(this EventData eventData)
        {
            var writeStream = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(eventData.Properties.Where(kvp => !SkipProperties.Contains(kvp.Key)).ToList(), writeStream);
            var result = writeStream.ToByteArray();
            writeStream.ReleaseBuffers();
            return result;
        }

        /// <summary>
        /// Deserializes event data properties
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        public static IDictionary<string, object> DeserializeProperties(this ArraySegment<byte> bytes)
        {
            var stream = new BinaryTokenStreamReader(bytes);
            return SerializationManager.Deserialize<List<KeyValuePair<string, object>>>(stream).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }
}
