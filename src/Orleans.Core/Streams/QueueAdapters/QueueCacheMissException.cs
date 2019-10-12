using System;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;

namespace Orleans.Streams
{
    /// <summary>
    /// Exception indicates that the requested message is not in the queue cache.
    /// </summary>
    [Serializable]
    public class QueueCacheMissException : DataNotAvailableException
    {
        private const string MESSAGE_FORMAT = "Item not found in cache.  Requested: {0}, Low: {1}, High: {2}";

        public string Requested { get; private set; }
        public string Low { get; private set; }
        public string High { get; private set; }

        public QueueCacheMissException() : this("Item no longer in cache") { }
        public QueueCacheMissException(string message) : base(message) { }
        public QueueCacheMissException(string message, Exception inner) : base(message, inner) { }

        public QueueCacheMissException(byte[] requested, in ArraySegment<byte> low, in ArraySegment<byte> high)
            : this(Convert.ToBase64String(requested), Convert.ToBase64String(low.ToArray()), Convert.ToBase64String(high.ToArray()))
        {
        }

        public QueueCacheMissException(StreamSequenceToken requested, StreamSequenceToken low, StreamSequenceToken high)
            : this(requested.ToString(), low.ToString(), high.ToString())
        {
        }

        public QueueCacheMissException(string requested, string low, string high)
            : this(string.Format(CultureInfo.InvariantCulture, MESSAGE_FORMAT, requested, low, high))
        {
            this.Requested = requested;
            this.Low = low;
            this.High = high;
        }

        public QueueCacheMissException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.Requested = info.GetString("Requested");
            this.Low = info.GetString("Low");
            this.High = info.GetString("High");
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("Requested", this.Requested);
            info.AddValue("Low", this.Low);
            info.AddValue("High", this.High);
            base.GetObjectData(info, context);
        }
    }
}
