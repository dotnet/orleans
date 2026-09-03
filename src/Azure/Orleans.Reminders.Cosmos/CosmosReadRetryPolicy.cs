using System.Net;
using Microsoft.Azure.Cosmos;

namespace Orleans.Reminders.Cosmos;

internal static class CosmosReadRetryPolicy
{
    internal const int MaxRetries = 2;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);

    public static async Task<TResult> ExecuteAsync<TResult>(
        Func<Task<TResult>> operation,
        Action<CosmosException, int, TimeSpan> onRetry,
        Func<TimeSpan, Task> delayAsync)
    {
        var retry = 0;
        while (true)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (CosmosException exception) when (
                exception.StatusCode == HttpStatusCode.RequestTimeout
                && retry < MaxRetries)
            {
                retry++;
                var delay = DefaultRetryDelay * retry;
                onRetry(exception, retry, delay);
                await delayAsync(delay).ConfigureAwait(false);
            }
        }
    }
}
