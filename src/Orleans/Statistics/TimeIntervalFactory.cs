namespace Orleans.Runtime
{
    internal static class TimeIntervalFactory
    {
        public static ITimeInterval CreateTimeInterval(bool measureFineGrainedTime)
        {
            return measureFineGrainedTime
                ? (ITimeInterval) new TimeIntervalStopWatchBased()
                : new TimeIntervalDateTimeBased();
        }
    }
}
