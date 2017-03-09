using System;

namespace Orleans
{
    internal class FixedBackoff : IBackoffProvider
    {
        private readonly TimeSpan fixedDelay;

        public FixedBackoff(TimeSpan delay)
        {
            fixedDelay = delay;
        }

        public TimeSpan Next(int attempt)
        {
            return fixedDelay;
        }
    }
}