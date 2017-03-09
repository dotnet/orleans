using System;
using System.Diagnostics;

namespace Orleans.Runtime
{
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
}