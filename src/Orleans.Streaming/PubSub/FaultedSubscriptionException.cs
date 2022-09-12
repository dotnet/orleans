using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// This exception indicates that an error has occurred on a stream subscription that has placed the subscription into
    ///  a faulted state.  Work on faulted subscriptions should be abandoned.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class FaultedSubscriptionException : OrleansException
    {
        private const string ErrorStringFormat =
            "Subscription is in a Faulted state.  Subscription:{0}, Stream:{1}";

        /// <summary>
        /// Initializes a new instance of the <see cref="FaultedSubscriptionException"/> class.
        /// </summary>
        public FaultedSubscriptionException() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="FaultedSubscriptionException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        public FaultedSubscriptionException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="FaultedSubscriptionException"/> class.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="streamId">The stream identifier.</param>
        internal FaultedSubscriptionException(GuidId subscriptionId, QualifiedStreamId streamId)
            : base(string.Format(ErrorStringFormat, subscriptionId.Guid, streamId)) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="FaultedSubscriptionException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The inner exception.</param>
        public FaultedSubscriptionException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="FaultedSubscriptionException"/> class.
        /// </summary>
        /// <param name="info">The serialization info.</param>
        /// <param name="context">The context.</param>
        private FaultedSubscriptionException(SerializationInfo info, StreamingContext context) : base(info, context) { }
    }
}
