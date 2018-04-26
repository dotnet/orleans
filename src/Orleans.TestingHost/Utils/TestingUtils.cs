using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Logging;
using Orleans.Serialization;

namespace Orleans.TestingHost.Utils
{
    /// <summary> Collection of test utilities </summary>
    public static class TestingUtils
    {
        /// <summary>
        /// Configure <paramref name="builder"/> with a <see cref="FileLoggerProvider"/> which logs to <paramref name="filePath"/>
        /// by default;
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="filePath"></param>
        public static void ConfigureDefaultLoggingBuilder(ILoggingBuilder builder, string filePath)
        {
            builder.AddFile(filePath);
        }

        /// <summary>
        /// Create trace file name for a specific node or client in a specific deployment
        /// </summary>
        /// <param name="nodeName"></param>
        /// <param name="clusterId"></param>
        /// <returns></returns>
        public static string CreateTraceFileName(string nodeName, string clusterId)
        {
            const string traceFileFolder = "logs";

            if (!Directory.Exists(traceFileFolder))
            {
                Directory.CreateDirectory(traceFileFolder);
            }

            var traceFileName = $"{traceFileFolder}\\{clusterId}_{nodeName}.log";

            return traceFileName;
        }

        /// <summary>
        /// Create the default logger factory, which would configure logger factory with a <see cref="FileLoggerProvider"/> that writes logs to <paramref name="filePath"/> and console.
        /// by default;
        /// </summary>
        /// <param name="filePath">the logger file path</param>
        /// <returns></returns>
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
        /// <returns>True if the predicate succeed, false otherwise</returns>
        public static async Task WaitUntilAsync(Func<bool,Task<bool>> predicate, TimeSpan timeout)
        {
            var keepGoing = new[] { true };
            Func<Task> loop =
                async () =>
                {
                    bool passed;
                    do
                    {
                        // need to wait a bit to before re-checking the condition.
                        await Task.Delay(TimeSpan.FromSeconds(1));
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

        /// <summary> Multipy a timeout by a value </summary>
        public static TimeSpan Multiply(TimeSpan time, double value)
        {
            double ticksD = checked(time.Ticks * value);
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
        }

        /// <summary> Configure the ThreadPool and the ServicePointManager for tests </summary>
        public static void ConfigureThreadPoolSettingsForStorageTests(int numDotNetPoolThreads = 200)
        {
            ThreadPool.SetMinThreads(numDotNetPoolThreads, numDotNetPoolThreads);
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = numDotNetPoolThreads; // 1000;
            ServicePointManager.UseNagleAlgorithm = false;
        }

        /// <summary> Serialize and deserialize the input </summary>
        /// <typeparam name="T">The type of the input</typeparam>
        /// <param name="input">The input to serialize and deserialize</param>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="serializationManager">The serialization manager.</param>
        /// <returns>Input that have been serialized and then deserialized</returns>
        public static T RoundTripDotNetSerializer<T>(T input, IGrainFactory grainFactory, SerializationManager serializationManager)
        {
            IFormatter formatter = new BinaryFormatter();
            MemoryStream stream = new MemoryStream(new byte[100000], true);
            formatter.Context = new StreamingContext(StreamingContextStates.All, new SerializationContext(serializationManager));
            formatter.Serialize(stream, input);
            stream.Position = 0;
            T output = (T)formatter.Deserialize(stream);

            return output;
        }
    }
}
