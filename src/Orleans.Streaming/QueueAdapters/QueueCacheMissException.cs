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
    public sealed class QueueCacheMissException : DataNotAvailableException
    {
        private const string MESSAGE_FORMAT = "Item not found in cache.  Requested: {0}, Low: {1}, High: {2}";

        /// <summary>
        /// Gets the requested sequence token.
        /// </summary>
        /// <value>The requested sequence token.</value>
        [Id(0)]
        public string Requested { get; private set; }

        /// <summary>
        /// Gets the earliest available sequence token.
        /// </summary>
        [Id(1)]
        public string Low { get; private set; }

        /// <summary>
        /// Gets the latest available sequence token.
        /// </summary>
        [Id(2)]
        public string High { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="QueueCacheMissException"/> class.
        /// </summary>
        public QueueCacheMissException() : this("Item no longer in cache") { }

        /// <summary>
        /// Initializes a new instance of the <see cref="QueueCacheMissException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        public QueueCacheMissException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="QueueCacheMissException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The inner exception.</param>
        public QueueCacheMissException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="QueueCacheMissException"/> class.
        /// </summary>
        /// <param name="requested">The requested sequence token.</param>
        /// <param name="low">The earliest available sequence token.</param>
        /// <param name="high">The latest available sequence token.</param>
        public QueueCacheMissException(StreamSequenceToken requested, StreamSequenceToken low, StreamSequenceToken high)
            : this(requested.ToString(), low.ToString(), high.ToString())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="QueueCacheMissException"/> class.
        /// </summary>
        /// <param name="requested">The requested sequence token.</param>
        /// <param name="low">The earliest available sequence token.</param>
        /// <param name="high">The latest available sequence token.</param>
        public QueueCacheMissException(string requested, string low, string high)
            : this(string.Format(CultureInfo.InvariantCulture, MESSAGE_FORMAT, requested, low, high))
        {
            Requested = requested;
            Low = low;
            High = high;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="QueueCacheMissException"/> class.
        /// </summary>
        /// <param name="info">The serialization info.</param>
        /// <param name="context">The context.</param>
        private QueueCacheMissException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            Requested = info.GetString("Requested");
            Low = info.GetString("Low");
            High = info.GetString("High");
        }

        /// <inheritdoc/>
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("Requested", Requested);
            info.AddValue("Low", Low);
            info.AddValue("High", High);
            base.GetObjectData(info, context);
        }
    }
}
