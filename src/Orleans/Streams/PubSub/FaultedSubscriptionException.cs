using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// This exception indicates that an error has occured on a stream subscription that has placed the subscription into
    ///  a faulted state.  Work on faulted subscriptions should be abandoned.
    /// </summary>
    [Serializable]
    public class FaultedSubscriptionException : OrleansException
    {
        private const string ErrorStringFormat =
            "Subscription is in a Faulted state.  Subscription:{0}, Stream:{1}";

        public FaultedSubscriptionException() { }
        public FaultedSubscriptionException(string message) : base(message) { }
        internal FaultedSubscriptionException(GuidId subscriptionId, StreamId streamId)
            : base(string.Format(ErrorStringFormat, subscriptionId.Guid, streamId)) { }
        public FaultedSubscriptionException(string message, Exception innerException) : base(message, innerException) { }
#if !NETSTANDARD
        public FaultedSubscriptionException(SerializationInfo info, StreamingContext context) : base(info, context) { }
#endif
    }
}
