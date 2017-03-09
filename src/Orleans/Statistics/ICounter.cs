namespace Orleans.Runtime
{
    /// <summary>
    /// A detailed statistic counter. Usually a low level performance statistic used in troubleshooting scenarios.
    /// </summary>
    public interface ICounter
    {
        /// <summary>
        /// the name of the statistic counter
        /// </summary>
        string Name { get; }

        /// <summary>
        /// if this the statistic counter value is delta since last value reported or an absolute value
        /// </summary>
        bool IsValueDelta { get; }
        string GetValueString();
        string GetDeltaString();
        void ResetCurrent();
        string GetDisplayString();
        CounterStorage Storage { get; }
        void TrackMetric(Logger logger);
    }
}