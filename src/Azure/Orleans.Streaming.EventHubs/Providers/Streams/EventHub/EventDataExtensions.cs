using System;
using System.Collections.Generic;
using System.Linq;
using Azure.Messaging.EventHubs;

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
        public static byte[] SerializeProperties(this EventData eventData, Serialization.Serializer serializer)
        {
            var result = serializer.SerializeToArray(eventData.Properties.Where(kvp => !string.Equals(kvp.Key, EventDataPropertyStreamNamespaceKey, StringComparison.Ordinal)).ToList());
            return result;
        }

        /// <summary>
        /// Deserializes event data properties
        /// </summary>
        public static IDictionary<string, object> DeserializeProperties(this ArraySegment<byte> bytes, Serialization.Serializer serializer)
        {
            return serializer.Deserialize<List<KeyValuePair<string, object>>>(bytes.AsSpan()).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }
}
