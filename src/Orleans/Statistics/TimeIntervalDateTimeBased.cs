using System;

namespace Orleans.Runtime
{
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