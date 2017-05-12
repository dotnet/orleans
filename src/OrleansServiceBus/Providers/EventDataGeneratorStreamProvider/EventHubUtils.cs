#if NETSTANDARD
using Microsoft.Azure.EventHubs;
using static Microsoft.Azure.EventHubs.EventData;
#else
using Microsoft.ServiceBus.Messaging;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.ServiceBus.Providers.Testing
{

    /// <summary>
    /// Setter for EventData members
    /// </summary>
    public static class EventDataProxyMethods
    {
        /// <summary>
        /// Set EventData.Offset
        /// </summary>
        /// <param name="eventData"></param>
        /// <param name="offSet"></param>
        public static void SetOffset(this EventData eventData, string offSet)
        {
            EventDataMethodCache.Instance.SetOffset(eventData, offSet);
        }

        /// <summary>
        /// Setter for EventData.SequenceNumber
        /// </summary>
        /// <param name="eventData"></param>
        /// <param name="sequenceNumber"></param>
        public static void SetSequenceNumber(this EventData eventData, long sequenceNumber)
        {
            EventDataMethodCache.Instance.SetSequenceNumber(eventData, sequenceNumber);
        }
        /// <summary>
        /// Setter for EventData.EnqueueTimeUtc
        /// </summary>
        /// <param name="eventData"></param>
        /// <param name="enqueueTime"></param>
        public static void SetEnqueuedTimeUtc(this EventData eventData, DateTime enqueueTime)
        {
            EventDataMethodCache.Instance.SetEnqueuedTimeUtc(eventData, enqueueTime);
        }
#if NETSTANDARD
        /// <summary>
        /// Setter for EventData.PartitionKey
        /// </summary>
        /// <param name="eventData"></param>
        /// <param name="partitionKey"></param>
        public static void SetPartitionKey(this EventData eventData, string partitionKey)
        {
            EventDataMethodCache.Instance.SetPartitionKey(eventData, partitionKey);
        }
#endif
    }
#if NETSTANDARD
    internal class EventDataMethodCache
    {
        public static EventDataMethodCache Instance = new EventDataMethodCache();
        private Action<object, object> systemPropertiesSetter; 
        private EventDataMethodCache()
        {
            var ignore = new EventData(new byte[1]);
            var systemPropertiesName = nameof(ignore.SystemProperties);
            this.systemPropertiesSetter = typeof(EventData).GetProperty(systemPropertiesName).SetValue;
        }
        private void SetEmptySystemPropertiesIfNull(EventData eventData)
        {
            if (eventData.SystemProperties == null)
            {
                var emptySystemProperties = SystemPropertiesCollectionMethodCache.Instance.Create();
                this.systemPropertiesSetter(eventData, emptySystemProperties);
            }
        }
        public void SetOffset(EventData eventData, string offSet)
        {
            SetEmptySystemPropertiesIfNull(eventData);
            SystemPropertiesCollectionMethodCache.Instance.SetOffset(eventData.SystemProperties, offSet);
        }

        public void SetSequenceNumber(EventData eventData, long sequenceNumber)
        {
            SetEmptySystemPropertiesIfNull(eventData);
            SystemPropertiesCollectionMethodCache.Instance.SetSequenceNumber(eventData.SystemProperties, sequenceNumber);
        }
        public void SetEnqueuedTimeUtc(EventData eventData, DateTime enqueueTime)
        {
            SetEmptySystemPropertiesIfNull(eventData);
            SystemPropertiesCollectionMethodCache.Instance.SetEnqueuedTimeUtc(eventData.SystemProperties, enqueueTime);
        }

        public void SetPartitionKey(EventData eventData, string partitionKey)
        {
            SetEmptySystemPropertiesIfNull(eventData);
            SystemPropertiesCollectionMethodCache.Instance.SetPartitionKey(eventData.SystemProperties, partitionKey);
        }
    }
    internal class SystemPropertiesCollectionMethodCache
    {
        public static SystemPropertiesCollectionMethodCache Instance = new SystemPropertiesCollectionMethodCache();
        private Action<object, object> offSetPropertySetter;
        private Action<object, object> sequenceNumberPropertySetter;
        private Action<object, object> enqueueTimeUtcPropertySetter;
        private Action<object, object> paritionKeyPropertySetter;
        private ConstructorInfo zeroArgConstructorInfo;
        private SystemPropertiesCollectionMethodCache()
        {
            EventData ignore = new EventData(new byte[1]);
            var offSetPropertyName = nameof(ignore.SystemProperties.Offset);
            var sequenceNumberPropertyName = nameof(ignore.SystemProperties.SequenceNumber);
            var enqueueTimeUtcPropertyName = nameof(ignore.SystemProperties.EnqueuedTimeUtc);
            var partitionKeyPropertyName = nameof(ignore.SystemProperties.PartitionKey);
            this.offSetPropertySetter = typeof(SystemPropertiesCollection).GetProperty(offSetPropertyName).SetValue;
            this.sequenceNumberPropertySetter = typeof(SystemPropertiesCollection).GetProperty(sequenceNumberPropertyName).SetValue;
            this.enqueueTimeUtcPropertySetter = typeof(SystemPropertiesCollection).GetProperty(enqueueTimeUtcPropertyName).SetValue;
            this.paritionKeyPropertySetter = typeof(SystemPropertiesCollection).GetProperty(partitionKeyPropertyName).SetValue;
            this.zeroArgConstructorInfo =
                typeof(SystemPropertiesCollection).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0];
        }
        public void SetOffset(SystemPropertiesCollection systemProperties, string offSet)
        {
            this.offSetPropertySetter(systemProperties, offSet);
        }

        public void SetSequenceNumber(SystemPropertiesCollection systemProperties, long sequenceNumber)
        {
            this.sequenceNumberPropertySetter(systemProperties, sequenceNumber);
        }
        public void SetEnqueuedTimeUtc(SystemPropertiesCollection systemProperties, DateTime enqueueTime)
        {
            this.enqueueTimeUtcPropertySetter(systemProperties, enqueueTime);
        }
        public void SetPartitionKey(SystemPropertiesCollection systemProperties, string paritionKey)
        {
            this.paritionKeyPropertySetter(systemProperties, paritionKey);
        }
        public SystemPropertiesCollection Create()
        {
            return (SystemPropertiesCollection)this.zeroArgConstructorInfo.Invoke(null);
        }
    }
#else
    internal class EventDataMethodCache
    {
        public static EventDataMethodCache Instance = new EventDataMethodCache();
        private Action<object, object> offSetPropertySetter;
        private Action<object, object> sequenceNumberPropertySetter;
        private Action<object, object> enqueueTimeUtcPropertySetter;

        private EventDataMethodCache()
        {
            var ignore = new EventData(new byte[1]);
            var offSetPropertyName = nameof(ignore.Offset);
            var sequenceNumberPropertyName = nameof(ignore.SequenceNumber);
            var enqueueTimeUtcPropertyName = nameof(ignore.EnqueuedTimeUtc);
            this.offSetPropertySetter = typeof(EventData).GetProperty(offSetPropertyName).SetValue;
            this.sequenceNumberPropertySetter = typeof(EventData).GetProperty(sequenceNumberPropertyName).SetValue;
            this.enqueueTimeUtcPropertySetter = typeof(EventData).GetProperty(enqueueTimeUtcPropertyName).SetValue;
        }

        public void SetOffset(EventData eventData, string offSet)
        {
            this.offSetPropertySetter(eventData, offSet);
        }

        public void SetSequenceNumber(EventData eventData, long sequenceNumber)
        {
            this.sequenceNumberPropertySetter(eventData, sequenceNumber);
        }
        public void SetEnqueuedTimeUtc(EventData eventData, DateTime enqueueTime)
        {
            this.enqueueTimeUtcPropertySetter(eventData, enqueueTime);
        }
    }
#endif
}
