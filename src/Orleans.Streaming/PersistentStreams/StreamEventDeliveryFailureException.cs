using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// This exception indicates that a stream event was not successfully delivered to the consumer.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class StreamEventDeliveryFailureException : OrleansException
    {
        private const string ErrorStringFormat =
            "Stream provider failed to deliver an event.  StreamProvider:{0}  Stream:{1}";

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamEventDeliveryFailureException"/> class.
        /// </summary>
        public StreamEventDeliveryFailureException() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamEventDeliveryFailureException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        public StreamEventDeliveryFailureException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamEventDeliveryFailureException"/> class.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        internal StreamEventDeliveryFailureException(QualifiedStreamId streamId)
            : base(string.Format(ErrorStringFormat, streamId.GetNamespace(), streamId.StreamId)) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamEventDeliveryFailureException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The inner exception.</param>
        public StreamEventDeliveryFailureException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamEventDeliveryFailureException"/> class.
        /// </summary>
        /// <param name="info">The serialization info.</param>
        /// <param name="context">The context.</param>
        public StreamEventDeliveryFailureException(SerializationInfo info, StreamingContext context) : base(info, context) { }
    }
}
