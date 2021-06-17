using System.Threading;
using System.Threading.Tasks;

namespace Presence.LoadGenerator
{
    /// <summary>
    /// Extension helpers for cancellation tokens.
    /// </summary>
    public static class CancellationTokenExtensions
    {
        /// <summary>
        /// Gets a task that completes when this cancellation token is cancelled.
        /// </summary>
        public static Task GetCompletionTask(this CancellationToken token)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = token.Register(() => completion.TrySetResult(true));
            return completion.Task;
        }
    }
}
