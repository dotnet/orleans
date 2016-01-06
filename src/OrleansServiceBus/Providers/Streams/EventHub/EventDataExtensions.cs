
using Microsoft.ServiceBus.Messaging;

namespace Orleans.ServiceBus.Providers.Streams.EventHub
{
    internal static class EventDataExtensions
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
    }
}
