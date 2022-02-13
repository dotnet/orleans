using System;

namespace Orleans
{
    internal class PeriodicAction
    {
        private readonly Action action;
        private readonly TimeSpan period;
        private DateTime nextUtc;

        public PeriodicAction(TimeSpan period, Action action, DateTime? start = null)
        {
            this.period = period;
            this.nextUtc = start ?? DateTime.UtcNow + period;
            this.action = action;
        }

        public bool TryAction(DateTime nowUtc)
        {
            if (nowUtc < this.nextUtc) return false;
            this.nextUtc = nowUtc + this.period;
            this.action();
            return true;
        }
    }
}
