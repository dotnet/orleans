using System;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// Callback interface that grains must implement in order to be able to register and receive Reminders.
    /// </summary>
    public interface IRemindable : IGrain
    {
        /// <summary>
        /// Receive a new Reminder.
        /// </summary>
        /// <param name="reminderName">Name of this Reminder</param>
        /// <param name="status">Status of this Reminder tick</param>
        /// <returns>Completion promise which the grain will resolve when it has finished processing this Reminder tick.</returns>
        Task ReceiveReminder(string reminderName, Runtime.TickStatus status);
    }

    namespace Runtime
    {
        /// <summary>
        /// Handle for a persistent Reminder.
        /// </summary>
        public interface IGrainReminder
        {
            /// <summary>
            /// Gets the name of this reminder.
            /// </summary>
            string ReminderName { get; }
        }

        /// <summary>
        /// The status of a tick when the tick is delivered to the registrar grain.
        /// In case of failures, it may happen that a tick is not delivered on time. The app can notice such missed ticks as follows.
        /// Upon receiving a tick, the app can calculate the theoretical number of ticks since start of the reminder as: 
        /// curCount = (Now - FirstTickTime) / Period
        /// The app can keep track of it as 'count'. Upon receiving a tick, the number of missed ticks = curCount - count - 1
        /// Thereafter, the app can set count = curCount
        /// </summary>
        [Serializable, GenerateSerializer, Immutable]
        public readonly struct TickStatus
        {
            /// <summary>
            /// Gets the time at which the first tick of this reminder is due, or was triggered.
            /// </summary>
            [Id(0)]
            public DateTime FirstTickTime { get; }

            /// <summary>
            /// Gets the period of the reminder.
            /// </summary>
            [Id(1)]
            public TimeSpan Period { get; }

            /// <summary>
            /// Gets the time on the runtime silo when the silo initiated the delivery of this tick.
            /// </summary>
            [Id(2)]
            public DateTime CurrentTickTime { get; }

            /// <summary>
            /// Creates a new <see cref="TickStatus"/> instance.
            /// </summary>
            /// <param name="firstTickTime">The time at which the first tick of the reminder is due.</param>
            /// <param name="period">The period of the reminder.</param>
            /// <param name="timeStamp">The time when delivery of the current tick was initiated.</param>
            /// <returns></returns>
            public TickStatus(DateTime firstTickTime, TimeSpan period, DateTime timeStamp)
            {
                FirstTickTime = firstTickTime;
                Period = period;
                CurrentTickTime = timeStamp;
            }

            /// <inheritdoc/>
            public override string ToString() => $"<{FirstTickTime}, {Period}, {CurrentTickTime}>";
        }

        /// <summary>
        /// Exception related to Orleans Reminder functions or Reminder service.
        /// </summary>
        [Serializable, GenerateSerializer]
        public sealed class ReminderException : OrleansException
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="ReminderException"/> class.
            /// </summary>
            /// <param name="message">The message.</param>
            public ReminderException(string message) : base(message) { }

            /// <summary>
            /// Initializes a new instance of the <see cref="ReminderException"/> class.
            /// </summary>
            /// <param name="info">The serialization info.</param>
            /// <param name="context">The context.</param>
            public ReminderException(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
            }
        }
    }
}
