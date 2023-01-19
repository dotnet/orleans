using System;
using System.Runtime.Serialization;

namespace Orleans.Reminders.Redis
{
    /// <summary>
    /// Exception thrown from <see cref="RedisReminderTable"/>.
    /// </summary>
    [GenerateSerializer]
    public class RedisRemindersException : Exception
    {
        /// <summary>
        /// Initializes a new instance of <see cref="RedisRemindersException"/>.
        /// </summary>
        public RedisRemindersException()
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="RedisRemindersException"/>.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        public RedisRemindersException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="RedisRemindersException"/>.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="inner">The exception that is the cause of the current exception, or a null reference (Nothing in Visual Basic) if no inner exception is specified.</param>
        public RedisRemindersException(string message, Exception inner) : base(message, inner)
        {
        }

        /// <inheritdoc />
        protected RedisRemindersException(
            SerializationInfo info,
            StreamingContext context) : base(info, context)
        {
        }
    }
}