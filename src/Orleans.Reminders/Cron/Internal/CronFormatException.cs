#nullable enable
using System;
#if !NETSTANDARD1_0
using System.Runtime.Serialization;
#endif

namespace Orleans.Reminders.Cron.Internal
{
    /// <summary>
    /// Represents an exception that's thrown, when invalid Cron expression is given.
    /// </summary>
#if !NETSTANDARD1_0
    [Serializable]
#endif
    internal class CronFormatException : FormatException
    {
        internal const string BaseMessage = "The given cron expression has an invalid format.";

        /// <summary>
        /// Initializes a new instance of the <see cref="CronFormatException"/> class.
        /// </summary>
        public CronFormatException() : this(BaseMessage)
        {
        }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="CronFormatException"/> class with
        /// a specified error message.
        /// </summary>
        public CronFormatException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CronFormatException"/> class with
        /// a specified error message and a reference to the inner exception that is the
        /// cause of this exception.
        /// </summary>
        public CronFormatException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

#if !NETSTANDARD1_0
        /// <inheritdoc />
#pragma warning disable SYSLIB0051
        protected CronFormatException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
#pragma warning restore SYSLIB0051
#endif
    }
}
