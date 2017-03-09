using System;

namespace Orleans.Runtime
{
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
}