#if NETSTANDARD
using Microsoft.Azure.EventHubs;
#else
using Microsoft.ServiceBus.Messaging;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.ServiceBus.Providers
{

    internal static class EventDataProxyMethods
    {
        public static void SetOffset(this EventData eventData, string offSet)
        {
            EventDataMethodCache.Instance.SetOffset(eventData, offSet);
        }

        public static void SetSequenceNumber(this EventData eventData, long sequenceNumber)
        {
            EventDataMethodCache.Instance.SetSequenceNumber(eventData, sequenceNumber);
        }
        public static void SetEnqueuedTimeUtc(this EventData eventData, DateTime enqueueTime)
        {
            EventDataMethodCache.Instance.SetEnqueuedTimeUtc(eventData, enqueueTime);
        }
#if NETSTANDARD
        public static void SetPartitionKey(this EventData eventData, string partitionKey)
        {
            EventDataMethodCache.Instance.SetPartitionKey(eventData, partitionKey);
        }
#endif
    }

    internal class EventDataMethodCache
    {
        public static EventDataMethodCache Instance = new EventDataMethodCache();
        private PropertyInfo offSetProperty;
        private PropertyInfo sequenceNumberProperty;
        private PropertyInfo enqueueTimeUtcProperty;
#if NETSTANDARD
        private PropertyInfo partitionKeyProperty;
#endif
        public EventDataMethodCache()
        {
            var sampleData = new EventData(new byte[1]);
#if NETSTANDARD
            var offSetPropertyName = nameof(sampleData.SystemProperties.Offset);
            var sequenceNumberPropertyName = nameof(sampleData.SystemProperties.SequenceNumber);
            var enqueueTimeUtcPropertyName = nameof(sampleData.SystemProperties.EnqueuedTimeUtc);
            var partitionKeyPropertyName = nameof(sampleData.SystemProperties.PartitionKey);

            this.partitionKeyProperty = typeof(EventData).GetProperty(partitionKeyPropertyName);
#else
            var offSetPropertyName = nameof(sampleData.Offset);
            var sequenceNumberPropertyName = nameof(sampleData.SequenceNumber);
            var enqueueTimeUtcPropertyName = nameof(sampleData.EnqueuedTimeUtc);
#endif
            this.offSetProperty = typeof(EventData).GetProperty(offSetPropertyName);
            this.sequenceNumberProperty = typeof(EventData).GetProperty(sequenceNumberPropertyName);
            this.enqueueTimeUtcProperty = typeof(EventData).GetProperty(enqueueTimeUtcPropertyName);

        }

        public void SetOffset(EventData eventData, string offSet)
        {
            this.offSetProperty.SetValue(eventData, offSet);
        }

        public void SetSequenceNumber(EventData eventData, long sequenceNumber)
        {
            this.sequenceNumberProperty.SetValue(eventData, sequenceNumber);
        }
        public void SetEnqueuedTimeUtc(EventData eventData, DateTime enqueueTime)
        {
            this.enqueueTimeUtcProperty.SetValue(eventData, enqueueTime);
        }
#if NETSTANDARD
        public void SetPartitionKey(EventData eventData, string partitionKey)
        {
            this.partitionKeyProperty.SetValue(eventData, partitionKey);
        }
#endif
    }
}
