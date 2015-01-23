using System;
using System.Globalization;
using System.Runtime.Serialization;

using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Exception indicates that the requested message is not in the queue cache.
    /// </summary>
    [Serializable]
    public class QueueAdapterCacheMissException : DataNotAvailableException
    {
        private const string MESSAGE_FORMAT = "Item not found in cache.  Requested: {0}, Low: {1}, High: {2}";

        public string Requested { get; private set; }
        public string Low { get; private set; }
        public string High { get; private set; }

        public QueueAdapterCacheMissException() : this("Item no longer in cache") { }
        public QueueAdapterCacheMissException(string message) : base(message) { }
        public QueueAdapterCacheMissException(string message, Exception inner) : base(message, inner) { }

        public QueueAdapterCacheMissException(EventSequenceToken requested, EventSequenceToken low, EventSequenceToken high)
            : this(requested.ToString(), low.ToString(), high.ToString())
        {
        }

        public QueueAdapterCacheMissException(string requested, string low, string high)
            : this(String.Format(CultureInfo.InvariantCulture, MESSAGE_FORMAT, requested, low, high))
        {
            Requested = requested;
            Low = low;
            High = high;
        }

        public QueueAdapterCacheMissException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            Requested = info.GetString("Requested");
            Low = info.GetString("Low");
            High = info.GetString("High");
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("Requested", Requested);
            info.AddValue("Low", Low);
            info.AddValue("High", High);
            base.GetObjectData(info, context);
        }
    }
}