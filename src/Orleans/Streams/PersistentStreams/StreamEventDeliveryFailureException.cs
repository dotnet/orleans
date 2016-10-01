using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// This exception indicates that a stream event was not successfully delivered to the consumer.
    /// </summary>
    [Serializable]
    public class StreamEventDeliveryFailureException : OrleansException
    {
        private const string ErrorStringFormat =
            "Stream provider failed to deliver an event.  StreamProvider:{0}  Stream:{1}";

        public StreamEventDeliveryFailureException() { }
        public StreamEventDeliveryFailureException(string message) : base(message) { }
        internal StreamEventDeliveryFailureException(StreamId streamId)
            : base(string.Format(ErrorStringFormat, streamId.ProviderName, streamId)) { }
        public StreamEventDeliveryFailureException(string message, Exception innerException) : base(message, innerException) { }
#if !NETSTANDARD
        public StreamEventDeliveryFailureException(SerializationInfo info, StreamingContext context) : base(info, context) { }
#endif
    }
}
