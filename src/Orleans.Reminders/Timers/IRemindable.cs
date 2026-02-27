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
        /// Represents the schedule type of a reminder tick.
        /// </summary>
        public enum ReminderScheduleKind : byte
        {
            /// <summary>
            /// A fixed interval reminder schedule.
            /// </summary>
            Interval = 0,

            /// <summary>
            /// A cron-based reminder schedule.
            /// </summary>
            Cron = 1
        }

        /// <summary>
        /// Priority of reminder processing.
        /// </summary>
        public enum ReminderPriority : byte
        {
            /// <summary>
            /// Default priority reminders.
            /// </summary>
            Normal = 0,

            /// <summary>
            /// High priority reminders.
            /// </summary>
            High = 1,
        }

        /// <summary>
        /// Action to apply when a reminder tick was missed.
        /// </summary>
        public enum MissedReminderAction : byte
        {
            /// <summary>
            /// Skip missed ticks and move to the next due occurrence.
            /// </summary>
            Skip = 0,

            /// <summary>
            /// Fire the reminder immediately.
            /// </summary>
            FireImmediately = 1,

            /// <summary>
            /// Notify about the miss without firing the reminder.
            /// </summary>
            Notify = 2,
        }

        /// <summary>
        /// Handle for a persistent Reminder.
        /// </summary>
        public interface IGrainReminder
        {
            /// <summary>
            /// Gets the name of this reminder.
            /// </summary>
            string ReminderName { get; }

            /// <summary>
            /// Gets the cron expression for this reminder.
            /// Returns <see cref="string.Empty"/> for interval-based reminders.
            /// </summary>
            string CronExpression { get; }

            /// <summary>
            /// Gets the cron time zone identifier for this reminder.
            /// Returns <see cref="string.Empty"/> for UTC scheduling or interval-based reminders.
            /// </summary>
            string CronTimeZone { get; }

            /// <summary>
            /// Gets the priority of this reminder.
            /// </summary>
            ReminderPriority Priority { get; }

            /// <summary>
            /// Gets the missed-tick behavior for this reminder.
            /// </summary>
            MissedReminderAction Action { get; }
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
            /// Gets the schedule kind for this reminder.
            /// </summary>
            [Id(3)]
            public ReminderScheduleKind ScheduleKind { get; }

            /// <summary>
            /// Creates a new <see cref="TickStatus"/> instance.
            /// </summary>
            /// <param name="firstTickTime">The time at which the first tick of the reminder is due.</param>
            /// <param name="period">The period of the reminder.</param>
            /// <param name="timeStamp">The time when delivery of the current tick was initiated.</param>
            /// <param name="scheduleKind">The schedule kind.</param>
            /// <returns></returns>
            public TickStatus(
                DateTime firstTickTime,
                TimeSpan period,
                DateTime timeStamp,
                ReminderScheduleKind scheduleKind = ReminderScheduleKind.Interval)
            {
                FirstTickTime = firstTickTime;
                Period = period;
                CurrentTickTime = timeStamp;
                ScheduleKind = scheduleKind;
            }

            /// <inheritdoc/>
            public override string ToString() => $"<{FirstTickTime}, {Period}, {CurrentTickTime}, {ScheduleKind}>";
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
            [Obsolete]
            public ReminderException(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
            }
        }
    }
}
