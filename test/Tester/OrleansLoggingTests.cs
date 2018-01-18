using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Logging;
using Orleans.Logging.Legacy;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester
{
    [TestCategory("BVT"), TestCategory("OrleansLogging")]
    public class OrleansLoggingTests : OrleansTestingBase, IClassFixture<DefaultClusterFixture>
    {
        private ITestOutputHelper output;

        public OrleansLoggingTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        //both value copied from EventBulkingDecoratorLogger
        private static readonly HashSet<int> excludedBulkLogCodes = new HashSet<int>(){
            0,
            100000 //internal runtime error code
        };
        private const int BulkEventSummaryOffset = 500000;


        [Fact]
#pragma warning disable 618
        public void OrleansLoggingCanConfigurePerCategoryServeriyOverrides()
        {
            //configure logging with severity overrides
            IServiceCollection serviceCollection = new ServiceCollection();
            serviceCollection.AddLogging();
            var severityOverrides = new OrleansLoggerSeverityOverrides();
            severityOverrides.LoggerSeverityOverrides.Add(this.GetType().FullName, Severity.Warning);
            serviceCollection.AddLogging(builder => builder.AddLegacyOrleansLogging(new List<ILogConsumer>()
            {
                new LegacyFileLogConsumer($"{this.GetType().Name}.log")
            }, severityOverrides));
            var serviceProvider = serviceCollection.BuildServiceProvider();
            //get logger
            var logger = serviceProvider.GetRequiredService<ILogger<OrleansLoggingTests>>();
            Assert.True(logger.IsEnabled(LogLevel.Warning));
            Assert.False(logger.IsEnabled(LogLevel.Information));

            //dispose log providers
            (serviceProvider as IDisposable)?.Dispose();
        }

        [Fact]
#pragma warning disable 618
        public void MicrosoftExtensionsLogging_LoggingFilter_CanAlsoConfigurePerCategoryLogLevel()
        {
            //configure logging with severity overrides
            IServiceCollection serviceCollection = new ServiceCollection();
            serviceCollection.AddLogging(builder => builder.AddLegacyOrleansLogging(new List<ILogConsumer>()
                {
                    new LegacyFileLogConsumer($"{this.GetType().Name}.log")
                })
                .AddFilter(this.GetType().FullName, LogLevel.Warning)
            );
            var serviceProvider = serviceCollection.BuildServiceProvider();
            //get logger
            var logger = serviceProvider.GetRequiredService<ILogger<OrleansLoggingTests>>();
            Assert.True(logger.IsEnabled(LogLevel.Warning));
            Assert.False(logger.IsEnabled(LogLevel.Information));

            //dispose log providers
            (serviceProvider as IDisposable)?.Dispose();
        }

        [Fact]
#pragma warning disable 618
        public async Task MicrosoftExtensionsLogging_Messagebulking_ShouldWork()
        {
            var statefulLogConsumer = new StatefulLogConsumer();
            var messageBulkingConfig = new EventBulkingOptions();
            messageBulkingConfig.BulkEventInterval = TimeSpan.FromSeconds(2);
            var serviceProvider = new ServiceCollection().AddLogging(builder =>
                    builder.AddLegacyOrleansLogging(new List<ILogConsumer>() {statefulLogConsumer}, null,
                        messageBulkingConfig))
                .BuildServiceProvider();
            var logger = serviceProvider.GetService<ILogger<OrleansLoggingTests>>();
            //the appearance of the same event
            var sameEventCount = messageBulkingConfig.BulkEventLimit + 5;
            var eventId = 5;
            var message = "Producing event 5";
            var count = 0;
            while (count++ < sameEventCount)
            {
                logger.LogInformation(eventId, message);
            }
            //same event message should only appear BulkMessageLimit times
            Assert.Equal(messageBulkingConfig.BulkEventLimit,
                statefulLogConsumer.ReceivedMessages.Where(m => m.Equals(message)).Count());
            await Task.Delay(TimeSpan.FromSeconds(3));
            logger.LogInformation(eventId, message);
            //after 3 seconds, the event cound summary message should be flushed to log consumers
            Assert.True(statefulLogConsumer.ReceivedMessages.Where(m => m.Contains("additional time(s) in previous"))
                            .Count() > 0);

            //dispose log providers
            (serviceProvider as IDisposable)?.Dispose();
        }

        [Fact]
        public void Logger_BulkMessageLimit_DifferentLoggers()
        {
            int n = 10000;
            var bulkEventOptions = new EventBulkingOptions();
            bulkEventOptions.BulkEventInterval = TimeSpan.FromMilliseconds(50);

            int logCode1 = 1;
            int logCode2 = 2;
            int expectedLogMessages1 = bulkEventOptions.BulkEventLimit + 2 + 1;
            int expectedLogMessages2 = bulkEventOptions.BulkEventLimit + 2;
            int expectedBulkLogMessages1 = 0;
            int expectedBulkLogMessages2 = 1;

            TestLogConsumer logConsumer = new TestLogConsumer(output);
            var serviceProvider = new ServiceCollection().AddLogging(builder =>
                    builder.AddLegacyOrleansLogging(new List<ILogConsumer>() { logConsumer }, null,
                        bulkEventOptions))
                .BuildServiceProvider();
            var logger1 = serviceProvider.GetService<ILoggerFactory>().CreateLogger("logger1");
            var logger2 = serviceProvider.GetService<ILoggerFactory>().CreateLogger("logger2");

            // Write log messages to logger #1
            for (int i = 0; i < bulkEventOptions.BulkEventLimit; i++)
            {
                logger1.Warn(logCode1, "Message " + (i + 1) + " to logger1");
            }

            // Write to logger#2 using same logCode -- This should NOT trigger the bulk message for logCode1
            logger2.Warn(logCode1, "Use same logCode to logger2");

            // Wait until the BulkMessageInterval time interval expires before writing the final log message - should cause any pending message flush);
            Thread.Sleep(bulkEventOptions.BulkEventInterval);
            Thread.Sleep(50);

            // Write log messages to logger #2
            for (int i = 0; i < n; i++)
            {
                logger2.Warn(logCode2, "Message " + (i + 1) + " to logger2");
            }

            // Wait until the BulkMessageInterval time interval expires before writing the final log message - should cause any pending message flush);
            Thread.Sleep(bulkEventOptions.BulkEventInterval);
            Thread.Sleep(50);

            logger1.Info(logCode1, "Penultimate message to logger1");
            logger2.Info(logCode2, "Penultimate message to logger2");

            logger1.Info(logCode1, "Final message to logger1");
            logger2.Info(logCode2, "Final message to logger2");

            Assert.Equal(expectedBulkLogMessages1,
                logConsumer.GetEntryCount(logCode1 + BulkEventSummaryOffset));
            Assert.Equal(expectedBulkLogMessages2,
                logConsumer.GetEntryCount(logCode2 + BulkEventSummaryOffset));
            Assert.Equal(expectedLogMessages1, logConsumer.GetEntryCount(logCode1));
            Assert.Equal(expectedLogMessages2, logConsumer.GetEntryCount(logCode2));
            Assert.Equal(0, logConsumer.GetEntryCount(logCode1 - 1)); //  "Should not see any other entries -1"
            Assert.Equal(0, logConsumer.GetEntryCount(logCode2 + 1)); //  "Should not see any other entries +1"

            //dispose log providers
            (serviceProvider as IDisposable)?.Dispose();
        }

        [Fact]
        public void Logger_BulkMessageLimit_DebugNotFiltered()
        {
            const string testName = "Logger_BulkMessageLimit_InfoNotFiltered";
            int n = 10000;
            var bulkEventOptions = new EventBulkingOptions();
            bulkEventOptions.BulkEventInterval = TimeSpan.FromMilliseconds(5);

            int logCode1 = 1;
            int expectedLogMessages1 = n + 2;
            int expectedBulkLogMessages1 = 0;

            TestLogConsumer logConsumer = new TestLogConsumer(output);
            //set minimum log level to debug
            var serviceProvider = new ServiceCollection().AddLogging(builder =>
                    builder.AddLegacyOrleansLogging(new List<ILogConsumer>() { logConsumer }, null,
                        bulkEventOptions)
                        .AddFilter(logLevel => logLevel >= LogLevel.Debug))
                .BuildServiceProvider();
            var logger1 = serviceProvider.GetService<ILoggerFactory>().CreateLogger(testName);

            // Write log messages to logger #1
            for (int i = 0; i < n; i++)
            {
                logger1.Debug(logCode1, "Debug message " + (i + 1) + " to logger1");
            }

            // Wait until the BulkMessageInterval time interval expires before writing the final log message - should cause any pending message flush);
            Thread.Sleep(bulkEventOptions.BulkEventInterval);
            Thread.Sleep(50);

            logger1.Info(logCode1, "Penultimate message to logger1");
            logger1.Info(logCode1, "Final message to logger1");

            Assert.Equal(expectedBulkLogMessages1,
                logConsumer.GetEntryCount(logCode1 + BulkEventSummaryOffset));
            Assert.Equal(expectedLogMessages1, logConsumer.GetEntryCount(logCode1));
            //dispose log providers
            (serviceProvider as IDisposable)?.Dispose();
        }

        [Fact]
        public void Logger_BulkMessageLimit_DifferentFinalLogCode()
        {
            const string testName = "Logger_BulkMessageLimit_DifferentFinalLogCode";
            int n = 100;
            var bulkEventOptions = new EventBulkingOptions();
            bulkEventOptions.BulkEventInterval = TimeSpan.FromMilliseconds(50);

            int mainLogCode = TestUtils.Random.Next();
            int finalLogCode = mainLogCode + 10;
            int expectedMainLogMessages = bulkEventOptions.BulkEventLimit;
            int expectedFinalLogMessages = 1;
            int expectedBulkLogMessages = 1;

            RunTestForLogFiltering(testName, n, mainLogCode, finalLogCode, expectedMainLogMessages,
                expectedFinalLogMessages, expectedBulkLogMessages, bulkEventOptions);
        }

        [Fact]
        public void Logger_BulkMessageLimit_SameFinalLogCode()
        {
            const string testName = "Logger_BulkMessageLimit_SameFinalLogCode";
            int n = 100;
            var bulkEventOptions = new EventBulkingOptions();
            bulkEventOptions.BulkEventInterval = TimeSpan.FromMilliseconds(50);

            int mainLogCode = TestUtils.Random.Next(100000);
            int finalLogCode = mainLogCode; // Same as main code
            int expectedMainLogMessages = bulkEventOptions.BulkEventLimit + 1; // == Loop + 1 final
            int expectedFinalLogMessages = expectedMainLogMessages;
            int expectedBulkLogMessages = 1;

            RunTestForLogFiltering(testName, n, mainLogCode, finalLogCode, expectedMainLogMessages,
                expectedFinalLogMessages, expectedBulkLogMessages, bulkEventOptions);
        }

        [Fact]
        public void Logger_BulkMessageLimit_Excludes_100000()
        {
            const string testName = "Logger_BulkMessageLimit_Excludes_100000";
            int n = 1000;
            var bulkEventOptions = new EventBulkingOptions();
            bulkEventOptions.BulkEventInterval = TimeSpan.FromMilliseconds(1);

            int mainLogCode = 100000;
            int finalLogCode = mainLogCode + 1;
            int expectedMainLogMessages = n;
            int expectedFinalLogMessages = 1;
            int expectedBulkLogMessages = 0;

            RunTestForLogFiltering(testName, n, finalLogCode, mainLogCode, expectedMainLogMessages,
                expectedFinalLogMessages, expectedBulkLogMessages, bulkEventOptions);
        }

        private void RunTestForLogFiltering(string testName, int n, int finalLogCode, int mainLogCode, int expectedMainLogMessages, 
            int expectedFinalLogMessages, int expectedBulkLogMessages, EventBulkingOptions eventBulkingOptions)
        {
            Assert.True(finalLogCode != (mainLogCode - 1), "Test code constraint -1");

            TestLogConsumer logConsumer = new TestLogConsumer(output);

            var serviceProvider = new ServiceCollection().AddLogging(builder =>
                    builder.AddLegacyOrleansLogging(new List<ILogConsumer>() { logConsumer }, null,
                        eventBulkingOptions))
                .BuildServiceProvider();
            var logger = serviceProvider.GetService<ILoggerFactory>().CreateLogger(testName);

            var stopwatch = new Stopwatch();
            stopwatch.Start();
            int tmp = 0;
            for (int i = 0; i < n; i++)
            {
                logger.Warn(mainLogCode, "msg " + i);
                tmp = i;
            }

            // Wait until the BulkMessageInterval time interval expires before wring the final log message - should cause bulk message flush);
            TimeSpan delay = eventBulkingOptions.BulkEventInterval - stopwatch.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                output.WriteLine("Sleeping for " + delay);
                Thread.Sleep(delay);
            }
            Thread.Sleep(50);

            logger.Info(finalLogCode, "Final msg");

            Assert.Equal(expectedMainLogMessages, logConsumer.GetEntryCount(mainLogCode));
            if (mainLogCode != finalLogCode)
            {
                Assert.Equal(expectedFinalLogMessages, logConsumer.GetEntryCount(finalLogCode));
            }
            Assert.Equal(expectedBulkLogMessages,
                logConsumer.GetEntryCount(mainLogCode + BulkEventSummaryOffset));
            Assert.Equal(0, logConsumer.GetEntryCount(mainLogCode - 1));  //  "Should not see any other entries -1"
            //dispose log providers
            (serviceProvider as IDisposable)?.Dispose();
        }

        public class TestLogConsumer : ILogConsumer
        {
            private readonly ITestOutputHelper output;
            private readonly bool traceToOutput;

            public TestLogConsumer(ITestOutputHelper output, bool traceToOutput = false)
            {
                this.output = output;
                this.traceToOutput = traceToOutput;
            }

            private readonly Dictionary<int, int> entryCounts = new Dictionary<int, int>();
            private int lastLogCode;

            public void Log(Severity severity, LoggerType loggerType, string caller, string message, System.Net.IPEndPoint myIPEndPoint, Exception exception, int eventCode = 0)
            {
                lock (this)
                {
                    if (entryCounts.ContainsKey(eventCode)) entryCounts[eventCode]++;
                    else entryCounts.Add(eventCode, 1);

                    if (eventCode != lastLogCode)
                    {
                        if (traceToOutput) output.WriteLine("{0} {1} - {2}", severity, eventCode, message);
                        lastLogCode = eventCode;
                    }
                }
            }

            internal int GetEntryCount(int eventCode)
            {
                lock (this)
                {
                    int count = (entryCounts.ContainsKey(eventCode)) ? entryCounts[eventCode] : 0;
                    output.WriteLine("Count {0} = {1}", eventCode, count);
                    return count;
                }
            }
        }

        public class StatefulLogConsumer : ILogConsumer
        {
            public IList<string> ReceivedMessages { get; private set; } = new List<string>();

            public void Log(Severity severity, LoggerType loggerType, string caller, string message,
                IPEndPoint ipEndPoint, Exception exception, int eventCode = 0)
            {
                this.ReceivedMessages.Add(message);
            }
        }
    }
}
