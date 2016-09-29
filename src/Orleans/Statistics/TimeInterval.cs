using System;
using System.Diagnostics;

namespace Orleans.Runtime
{
    internal interface ITimeInterval
    {
        void Start();

        void Stop();

        void Restart();

        TimeSpan Elapsed { get; }
    }

    internal static class TimeIntervalFactory
    {
        public static ITimeInterval CreateTimeInterval(bool measureFineGrainedTime)
        {
            return measureFineGrainedTime
                ? (ITimeInterval) new TimeIntervalStopWatchBased()
                : new TimeIntervalDateTimeBased();
        }
    }

    internal class TimeIntervalStopWatchBased : ITimeInterval
    {
        private readonly Stopwatch stopwatch;

        public TimeIntervalStopWatchBased()
        {
            stopwatch = new Stopwatch();
        }

        public void Start()
        {
            stopwatch.Start();
        }

        public void Stop()
        {
            stopwatch.Stop();
        }

        public void Restart()
        {
            stopwatch.Restart();

        }
        public TimeSpan Elapsed { get { return stopwatch.Elapsed; } }
    }

    internal class TimeIntervalDateTimeBased : ITimeInterval
    {
        private bool running;
        private DateTime start;

        public TimeSpan Elapsed { get; private set; }

        public TimeIntervalDateTimeBased()
        {
            running = false;
            Elapsed = TimeSpan.Zero;
        }

        public void Start()
        {
            if (running) return;

            start = DateTime.UtcNow;
            running = true;
            Elapsed = TimeSpan.Zero;
        }

        public void Stop()
        {
            if (!running) return;

            var end = DateTime.UtcNow;
            Elapsed += (end - start);
            running = false;
        }

        public void Restart()
        {
            start = DateTime.UtcNow;
            running = true;
            Elapsed = TimeSpan.Zero;
        }
    }
}
