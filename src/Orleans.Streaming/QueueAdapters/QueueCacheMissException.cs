using System;
using System.Globalization;
using System.Runtime.Serialization;

namespace Orleans.Streams
{
    /// <summary>
    /// Exception indicates that the requested message is not in the queue cache.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class QueueCacheMissException : DataNotAvailableException
    {
        private const string MESSAGE_FORMAT = "Item not found in cache.  Requested: {0}, Low: {1}, High: {2}";

        [Id(0)]
        public string Requested { get; private set; }
        [Id(1)]
        public string Low { get; private set; }
        [Id(2)]
        public string High { get; private set; }

        public QueueCacheMissException() : this("Item no longer in cache") { }
        public QueueCacheMissException(string message) : base(message) { }
        public QueueCacheMissException(string message, Exception inner) : base(message, inner) { }

        public QueueCacheMissException(StreamSequenceToken requested, StreamSequenceToken low, StreamSequenceToken high)
            : this(requested.ToString(), low.ToString(), high.ToString())
        {
        }

        public QueueCacheMissException(string requested, string low, string high)
            : this(String.Format(CultureInfo.InvariantCulture, MESSAGE_FORMAT, requested, low, high))
        {
            Requested = requested;
            Low = low;
            High = high;
        }

        public QueueCacheMissException(SerializationInfo info, StreamingContext context)
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
