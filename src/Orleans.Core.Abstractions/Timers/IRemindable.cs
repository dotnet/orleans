using System;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// Callback interface that grains must implement inorder to be able to register and receive Reminders.
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
            /// <summary> Name of this Reminder. </summary>
            string ReminderName { get; }
        }

        /// <summary>
        /// The status of a tick when the tick is delivered to the registrar grain.
        /// In case of failures, it may happen that a tick is not delivered on time. The app can notice such missed missed ticks as follows.
        /// Upon receiving a tick, the app can calculate the theoretical number of ticks since start of the reminder as: 
        /// curCount = (Now - FirstTickTime) / Period
        /// The app can keep track of it as 'count'. Upon receiving a tick, the number of missed ticks = curCount - count - 1
        /// Thereafter, the app can set count = curCount
        /// </summary>
        [Serializable]
        public struct TickStatus
        {
            /// <summary>
            /// The time at which the first tick of this reminder is due, or was triggered
            /// </summary>
            public DateTime FirstTickTime { get; private set; }

            /// <summary>
            /// The period of the reminder
            /// </summary>
            public TimeSpan Period { get; private set; }

            /// <summary>
            /// The time on the runtime silo when the silo initiated the delivery of this tick.
            /// </summary>
            public DateTime CurrentTickTime { get; private set; }

            internal static TickStatus NewStruct(DateTime firstTickTime, TimeSpan period, DateTime timeStamp)
            {
                return
                    new TickStatus
                        {
                            FirstTickTime = firstTickTime,
                            Period = period,
                            CurrentTickTime = timeStamp
                        };
            }

            public override String ToString()
            {
                return String.Format("<{0}, {1}, {2}>", FirstTickTime, Period, CurrentTickTime);
            }
        }

        #region App visible exceptions

        /// <summary>
        /// Exception related to Orleans Reminder functions or Reminder service.
        /// </summary>
        [Serializable]
        public class ReminderException : OrleansException
        {
            public ReminderException(string msg) : base(msg) { }

            public ReminderException(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
            }
        }

        #endregion
    }
}
