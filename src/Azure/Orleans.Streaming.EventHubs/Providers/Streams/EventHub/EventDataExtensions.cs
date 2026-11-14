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
        /// <param name="eventData">The event data to update.</param>
        /// <param name="streamNamespace">The stream namespace value to store.</param>
        public static void SetStreamNamespaceProperty(this EventData eventData, string? streamNamespace)
        {
            ArgumentNullException.ThrowIfNull(eventData);
            eventData.Properties[EventDataPropertyStreamNamespaceKey] = streamNamespace;
        }

        /// <summary>
        /// Gets stream namespace from the EventData
        /// </summary>
        /// <param name="eventData">The event data to read.</param>
        /// <returns>The stored stream namespace, or <see langword="null"/> if none is present.</returns>
        public static string? GetStreamNamespaceProperty(this EventData eventData)
        {
            ArgumentNullException.ThrowIfNull(eventData);
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
        /// <param name="eventData">The event data containing the properties to serialize.</param>
        /// <param name="serializer">The serializer used to serialize application properties.</param>
        public static byte[] SerializeProperties(this EventData eventData, Serialization.Serializer serializer)
        {
            ArgumentNullException.ThrowIfNull(eventData);
            ArgumentNullException.ThrowIfNull(serializer);
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
        /// <param name="bytes">The serialized properties.</param>
        /// <param name="serializer">The serializer used to deserialize application properties.</param>
        public static IDictionary<string, object> DeserializeProperties(this ArraySegment<byte> bytes, Serialization.Serializer serializer)
        {
            ArgumentNullException.ThrowIfNull(serializer);
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
