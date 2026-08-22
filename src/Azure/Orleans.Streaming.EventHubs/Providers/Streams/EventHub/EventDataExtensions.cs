using System;
using System.Collections.Generic;
using Azure.Messaging.EventHubs;

namespace Orleans.Streaming.EventHubs
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
        public static void SetStreamNamespaceProperty(this EventData eventData, string? streamNamespace)
        {
            eventData.Properties[EventDataPropertyStreamNamespaceKey] = streamNamespace;
        }

        /// <summary>
        /// Gets stream namespace from the EventData
        /// </summary>
        /// <param name="eventData"></param>
        /// <returns></returns>
        public static string? GetStreamNamespaceProperty(this EventData eventData)
        {
            object? namespaceObj;
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
            if (eventData.Properties.Count == 0
                || (eventData.Properties.Count == 1 && eventData.Properties.ContainsKey(EventDataPropertyStreamNamespaceKey)))
            {
                return [];
            }

            var properties = new List<KeyValuePair<string, object>>(eventData.Properties.Count);
            foreach (var property in eventData.Properties)
            {
                if (!string.Equals(property.Key, EventDataPropertyStreamNamespaceKey, StringComparison.Ordinal))
                {
                    properties.Add(property);
                }
            }

            return serializer.SerializeToArray(properties);
        }

        /// <summary>
        /// Deserializes event data properties
        /// </summary>
        public static IDictionary<string, object> DeserializeProperties(this ArraySegment<byte> bytes, Serialization.Serializer serializer)
        {
            if (bytes.Count == 0)
            {
                return new Dictionary<string, object>();
            }

            var properties = serializer.Deserialize<List<KeyValuePair<string, object>>>(bytes.AsSpan())!;
            var result = new Dictionary<string, object>(properties.Count);
            foreach (var property in properties)
            {
                result.Add(property.Key, property.Value);
            }

            return result;
        }
    }
}
