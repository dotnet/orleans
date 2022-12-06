using Orleans.Runtime;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Tester.Redis.Utility
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

            var timeoutCancellationTokenSource = new CancellationTokenSource();
            var completedTask = await Task.WhenAny(taskToComplete, Task.Delay(timeout, timeoutCancellationTokenSource.Token));

            // We got done before the timeout, or were able to complete before this code ran, return the result
            if (taskToComplete == completedTask)
            {
                timeoutCancellationTokenSource.Cancel();
                // Await this so as to propagate the exception correctly
                await taskToComplete;
                return;
            }

            // We did not complete before the timeout, we fire and forget to ensure we observe any exceptions that may occur
            taskToComplete.Ignore();
            throw new TimeoutException(string.Format("WithTimeout has timed out after {0}.", timeout));
        }
    }

    public static class SiloAddressUtils
    {
        private static readonly IPEndPoint localEndpoint = new IPEndPoint(IPAddress.Loopback, 0);

        public static SiloAddress NewLocalSiloAddress(int gen)
        {
            return SiloAddress.New(localEndpoint, gen);
        }
    }
}