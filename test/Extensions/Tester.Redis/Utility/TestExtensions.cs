using Orleans.Runtime;
using System.Net;

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

            if (taskToComplete == completedTask)
            {
                timeoutCancellationTokenSource.Cancel();
                await taskToComplete;
                return;
            }

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