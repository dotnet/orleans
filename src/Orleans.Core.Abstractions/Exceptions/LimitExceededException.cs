using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Signifies that a grain is in an overloaded state where some runtime limit setting is currently being exceeded, 
    /// and so that grain is unable to currently accept the message being sent.
    /// </summary>
    /// <remarks>
    /// This situation is often a transient condition.
    /// The message is likely to be accepted by this grain if it is retransmitted at a later time.
    /// </remarks>
    [Serializable]
    [GenerateSerializer]
    public class LimitExceededException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LimitExceededException"/> class.
        /// </summary>
        public LimitExceededException()
            : base("Limit exceeded")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LimitExceededException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        public LimitExceededException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LimitExceededException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        public LimitExceededException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LimitExceededException"/> class.
        /// </summary>
        /// <param name="limitName">
        /// The limit name.
        /// </param>
        /// <param name="current">
        /// The current value.
        /// </param>
        /// <param name="threshold">
        /// The threshold value.
        /// </param>
        /// <param name="extraInfo">
        /// Extra, descriptive information.
        /// </param>
        public LimitExceededException(string limitName, int current, int threshold, object extraInfo)
            : base($"Limit exceeded {limitName} Current={current} Threshold={threshold} {extraInfo}")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LimitExceededException"/> class.
        /// </summary>
        /// <param name="info">
        /// The serialization info.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        protected LimitExceededException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}

