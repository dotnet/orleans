using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.TestingHost.Utils
{
    public static class TestingUtils
    {
        public static async Task WaitUntilAsync(Func<bool,Task<bool>> predicate, TimeSpan timeout)
        {
            var keepGoing = new[] { true };
            Func<Task> loop =
                async () =>
                {
                    do
                    {
                        // need to wait a bit to before re-checking the condition.
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                    while (!await predicate(!keepGoing[0]) && keepGoing[0]);
                };

            var task = loop();
            try
            {
                await Task.WhenAny(task, Task.Delay(timeout));
            }
            finally
            {
                keepGoing[0] = false;
            }

            await task;
        }

        public static TimeSpan Multiply(TimeSpan time, double value)
        {
            double ticksD = checked(time.Ticks * value);
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
        }

        public static void ConfigureThreadPoolSettingsForStorageTests(int numDotNetPoolThreads = 200)
        {
            ThreadPool.SetMinThreads(numDotNetPoolThreads, numDotNetPoolThreads);
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = numDotNetPoolThreads; // 1000;
            ServicePointManager.UseNagleAlgorithm = false;
        }

        public static async Task WithTimeout(this Task taskToComplete, TimeSpan timeout, string message)
        {
            if (taskToComplete.IsCompleted)
            {
                await taskToComplete;
                return;
            }

            await Task.WhenAny(taskToComplete, Task.Delay(timeout));

            // We got done before the timeout, or were able to complete before this code ran, return the result
            if (taskToComplete.IsCompleted)
            {
                // Await this so as to propagate the exception correctly
                await taskToComplete;
                return;
            }

            // We did not complete before the timeout, we fire and forget to ensure we observe any exceptions that may occur
            taskToComplete.Ignore();
            throw new TimeoutException(message);
        }

        public static T RoundTripDotNetSerializer<T>(T input)
        {
            IFormatter formatter = new BinaryFormatter();
            MemoryStream stream = new MemoryStream(new byte[100000], true);
            formatter.Serialize(stream, input);
            stream.Position = 0;
            T output = (T)formatter.Deserialize(stream);
            return output;
        }
    }
}
