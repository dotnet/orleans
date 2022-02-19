using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost.Logging;

namespace Orleans.TestingHost.Utils
{
    /// <summary> Collection of test utilities </summary>
    public static class TestingUtils
    {
        private static long uniquifier = Stopwatch.GetTimestamp();

        /// <summary>
        /// Configure <paramref name="builder" /> with a <see cref="FileLoggerProvider" /> which logs to <paramref name="filePath" />
        /// by default;
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filePath">The file path.</param>
        public static void ConfigureDefaultLoggingBuilder(ILoggingBuilder builder, string filePath)
        {
            builder.AddFile(filePath);
        }

        /// <summary>
        /// Create trace file name for a specific node or client in a specific deployment
        /// </summary>
        /// <param name="nodeName">Name of the node.</param>
        /// <param name="clusterId">The cluster identifier.</param>
        /// <returns>The new trace file name.</returns>
        public static string CreateTraceFileName(string nodeName, string clusterId)
        {
            const string traceFileFolder = "logs";

            if (!Directory.Exists(traceFileFolder))
            {
                Directory.CreateDirectory(traceFileFolder);
            }

            var traceFileName = Path.Combine(traceFileFolder, $"{clusterId}_{Interlocked.Increment(ref uniquifier):X}_{nodeName}.log");

            return traceFileName;
        }

        /// <summary>
        /// Create the default logger factory, which would configure logger factory with a <see cref="FileLoggerProvider" /> that writes logs to <paramref name="filePath" /> and console.
        /// by default;
        /// </summary>
        /// <param name="filePath">The file path.</param>
        /// <returns>ILoggerFactory.</returns>
        public static ILoggerFactory CreateDefaultLoggerFactory(string filePath)
        {
            return CreateDefaultLoggerFactory(filePath, new LoggerFilterOptions());
        }

        /// <summary>
        /// Create the default logger factory, which would configure logger factory with a <see cref="FileLoggerProvider"/> that writes logs to <paramref name="filePath"/> and console.
        /// by default;
        /// </summary>
        /// <param name="filePath">the logger file path</param>
        /// <param name="filters">log filters you want to configure your logging with</param>
        /// <returns></returns>
        public static ILoggerFactory CreateDefaultLoggerFactory(string filePath, LoggerFilterOptions filters)
        {
            var factory = new LoggerFactory(new List<ILoggerProvider>(), filters);
            factory.AddProvider(new FileLoggerProvider(filePath));
            return factory;
        }

        /// <summary> Run the predicate until it succeed or times out </summary>
        /// <param name="predicate">The predicate to run</param>
        /// <param name="timeout">The timeout value</param>
        /// <param name="delayOnFail">The time to delay next call upon failure</param>
        /// <returns>True if the predicate succeed, false otherwise</returns>
        public static async Task WaitUntilAsync(Func<bool,Task<bool>> predicate, TimeSpan timeout, TimeSpan? delayOnFail = null)
        {
            delayOnFail = delayOnFail ?? TimeSpan.FromSeconds(1);
            var keepGoing = new[] { true };
            Func<Task> loop =
                async () =>
                {
                    bool passed;
                    do
                    {
                        // need to wait a bit to before re-checking the condition.
                        await Task.Delay(delayOnFail.Value);
                        passed = await predicate(false);
                    }
                    while (!passed && keepGoing[0]);
                    if(!passed)
                        await predicate(true);
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

        /// <summary>
        /// Multiply a timeout by a value
        /// </summary>
        /// <param name="time">The time.</param>
        /// <param name="value">The value.</param>
        /// <returns>The resulting time span value.</returns>
        public static TimeSpan Multiply(TimeSpan time, double value)
        {
            double ticksD = checked(time.Ticks * value);
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
        }

        /// <summary>
        /// Configures the <see cref="ThreadPool"/> and the <see cref="ServicePointManager"/> for tests.
        /// </summary>
        /// <param name="numDotNetPoolThreads">The minimum number of <see cref="ThreadPool"/> threads.</param>
        public static void ConfigureThreadPoolSettingsForStorageTests(int numDotNetPoolThreads = 200)
        {
            ThreadPool.SetMinThreads(numDotNetPoolThreads, numDotNetPoolThreads);
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = numDotNetPoolThreads; // 1000;
            ServicePointManager.UseNagleAlgorithm = false;
        }
    }
}
