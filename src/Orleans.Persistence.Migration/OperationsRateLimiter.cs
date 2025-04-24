using Microsoft.Extensions.Logging;

namespace Orleans.Persistence.Migration
{
    internal class OperationsRateLimiter
    {
        private int maxOpsPerMinute;
        private int opsThisMinute = 0;
        private DateTime lastReset = DateTime.UtcNow;

        public OperationsRateLimiter(int maxOpsPerMinute)
        {
            UpdateLimit(maxOpsPerMinute);
        }

        public void UpdateLimit(int newLimit)
        {
            if (newLimit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newLimit), "Must be > 0");
            }

            maxOpsPerMinute = newLimit;
            opsThisMinute = 0;
            lastReset = DateTime.UtcNow;
        }

        public async Task WaitIfNeededAsync(int increment, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;

            if ((now - lastReset).TotalMinutes >= 1)
            {
                opsThisMinute = 0;
                lastReset = now;
            }

            if (opsThisMinute >= maxOpsPerMinute)
            {
                var delay = 60000 - (int)(now - lastReset).TotalMilliseconds;
                if (delay > 0)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                opsThisMinute = 0;
                lastReset = DateTime.UtcNow;
            }

            opsThisMinute += increment;
        }
    }
}
