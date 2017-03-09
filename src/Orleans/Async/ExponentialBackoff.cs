using System;
using Orleans.Runtime;

namespace Orleans
{
    internal class ExponentialBackoff : IBackoffProvider
    {
        private readonly TimeSpan minDelay;
        private readonly TimeSpan maxDelay;
        private readonly TimeSpan step;
        private readonly SafeRandom random;

        public ExponentialBackoff(TimeSpan minDelay, TimeSpan maxDelay, TimeSpan step)
        {
            if (minDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("minDelay", minDelay, "ExponentialBackoff min delay must be a positive number.");
            if (maxDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("maxDelay", maxDelay, "ExponentialBackoff max delay must be a positive number.");
            if (step <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("step", step, "ExponentialBackoff step must be a positive number.");
            if (minDelay >= maxDelay) throw new ArgumentOutOfRangeException("minDelay", minDelay, "ExponentialBackoff min delay must be greater than max delay.");

            this.minDelay = minDelay;
            this.maxDelay = maxDelay;
            this.step = step;
            this.random = new SafeRandom();
        }

        public TimeSpan Next(int attempt)
        {
            TimeSpan currMax;
            try
            {
                long multiple = checked(1 << attempt);
                currMax = minDelay + step.Multiply(multiple); // may throw OverflowException
                if (currMax <= TimeSpan.Zero)
                    throw new OverflowException();
            }
            catch (OverflowException)
            {
                currMax = maxDelay;
            }
            currMax = StandardExtensions.Min(currMax, maxDelay);

            if (minDelay >= currMax) throw new ArgumentOutOfRangeException(String.Format("minDelay {0}, currMax = {1}", minDelay, currMax));
            return random.NextTimeSpan(minDelay, currMax);
        }
    }
}