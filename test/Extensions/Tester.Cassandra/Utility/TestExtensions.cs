namespace Tester.Cassandra.Utility
{
    public static class TestExtensions
    {
        public static async Task WithTimeout(this Task taskToComplete, TimeSpan timeout)
        {
            if (taskToComplete.IsCompleted)
            {
                await taskToComplete;
                return;
            }

            using var timeoutCancellationTokenSource = new CancellationTokenSource();
            var completedTask = await Task.WhenAny(taskToComplete, Task.Delay(timeout, timeoutCancellationTokenSource.Token));

            if (taskToComplete == completedTask)
            {
                timeoutCancellationTokenSource.Cancel();
                await taskToComplete;
                return;
            }

            taskToComplete.Ignore();
            throw new TimeoutException(string.Format("WithTimeout has timed out after {0}.", timeout));
        }

        public static async Task<T> WithTimeout<T>(this Task<T> taskToComplete, TimeSpan timeout)
        {
            if (taskToComplete.IsCompleted)
            {
                return await taskToComplete;
            }

            using var timeoutCancellationTokenSource = new CancellationTokenSource();
            var completedTask = await Task.WhenAny(taskToComplete, Task.Delay(timeout, timeoutCancellationTokenSource.Token));

            if (taskToComplete == completedTask)
            {
                timeoutCancellationTokenSource.Cancel();
                return await taskToComplete;
            }

            taskToComplete.Ignore();
            throw new TimeoutException(string.Format("WithTimeout has timed out after {0}.", timeout));
        }
    }
}