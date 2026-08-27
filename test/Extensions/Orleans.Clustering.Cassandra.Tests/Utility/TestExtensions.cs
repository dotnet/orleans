namespace Tester.Cassandra.Utility
{
    public static class TestExtensions
    {
        public static async Task WithTimeout(
            this Task taskToComplete,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutCancellationTokenSource = new CancellationTokenSource(timeout);
            using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellationTokenSource.Token,
                cancellationToken);

            try
            {
                await taskToComplete.WaitAsync(linkedCancellationTokenSource.Token);
            }
            catch (OperationCanceledException) when (
                timeoutCancellationTokenSource.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                if (taskToComplete.IsCompleted)
                {
                    await taskToComplete;
                    return;
                }

                taskToComplete.Ignore();
                throw new TimeoutException(string.Format("WithTimeout has timed out after {0}.", timeout));
            }
        }

        public static async Task<T> WithTimeout<T>(
            this Task<T> taskToComplete,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutCancellationTokenSource = new CancellationTokenSource(timeout);
            using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellationTokenSource.Token,
                cancellationToken);

            try
            {
                return await taskToComplete.WaitAsync(linkedCancellationTokenSource.Token);
            }
            catch (OperationCanceledException) when (
                timeoutCancellationTokenSource.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                if (taskToComplete.IsCompleted)
                {
                    return await taskToComplete;
                }

                taskToComplete.Ignore();
                throw new TimeoutException(string.Format("WithTimeout has timed out after {0}.", timeout));
            }
        }
    }
}