
using System;
using System.Collections.Generic;
using Microsoft.ServiceBus.Messaging;
using Orleans.Serialization;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Extends EventData to support streaming
    /// </summary>
    public static class EventDataExtensions
    {
        public const string EventDataPropertyStreamNamespaceKey = "StreamNamespace";

        public static void SetStreamNamespaceProperty(this EventData eventData, string streamNamespace)
        {
            eventData.Properties[EventDataPropertyStreamNamespaceKey] = streamNamespace;
        }

        public static string GetStreamNamespaceProperty(this EventData eventData)
        {
            object namespaceObj;
            if (eventData.Properties.TryGetValue(EventDataPropertyStreamNamespaceKey, out namespaceObj))
            {
                return (string)namespaceObj;
            }
            return null;
        }

        public static byte[] SerializeProperties(this IDictionary<string, object> properties)
        {
            var writeStream = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(properties, writeStream);
            return writeStream.ToByteArray();
        }

        public static IDictionary<string, object> DeserializeProperties(this ArraySegment<byte> bytes)
        {
            var stream = new BinaryTokenStreamReader(bytes);
            return SerializationManager.Deserialize<IDictionary<string, object>>(stream);
        }
    }
}
