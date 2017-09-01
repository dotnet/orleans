using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Tester;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests
{
    /// <summary>
    ///This is a test class for LoggerTest and is intended
    ///to contain all LoggerTest Unit Tests
    ///</summary>
    public class LoggerTest : OrleansTestingBase, IClassFixture<DefaultClusterFixture>, IDisposable
    {
        private readonly ITestOutputHelper output;
        private double timingFactor;
        private DefaultClusterFixture fixture;

        public LoggerTest(ITestOutputHelper output, DefaultClusterFixture fixture)
        {
            this.fixture = fixture;
            this.output = output;
            LogManager.UnInitialize();
            LogManager.SetRuntimeLogLevel(Severity.Verbose);
            LogManager.SetAppLogLevel(Severity.Info);
            var overrides = new [] {
                new Tuple<string, Severity>("Runtime.One", Severity.Warning),
                new Tuple<string, Severity>("Grain.Two", Severity.Verbose3)
            };
            LogManager.SetTraceLevelOverrides(overrides.ToList());
            timingFactor = TestUtils.CalibrateTimings();
        }

        public void Dispose()
        {
            LogManager.Flush();
            LogManager.UnInitialize();
        }

        /// <summary>
        ///A test for Logger Constructor
        ///</summary>
        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_ConstructorTest()
        {
            Logger target = LogManager.GetLogger("Test", LoggerType.Runtime);
            Assert.Equal(Severity.Verbose,  target.SeverityLevel);  // "Default severity level not calculated correctly for runtime logger with no overrides"

            target = LogManager.GetLogger("One", LoggerType.Runtime);
            Assert.Equal(Severity.Warning, target.SeverityLevel);  // "Default severity level not calculated correctly for runtime logger with an override"

            target = LogManager.GetLogger("Two", LoggerType.Grain);
            Assert.Equal(Severity.Verbose3, target.SeverityLevel);  // "Default severity level not calculated correctly for runtime logger with an override"
        }

        /// <summary>
        ///A test for Logger Constructor
        ///</summary>
        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_ConstructorTest1()
        {
            Logger target = LogManager.GetLogger("Test", LoggerType.Application);
            Assert.Equal(Severity.Info,  target.SeverityLevel);  //  "Default severity level not calculated correctly for application logger with no override"

            target = LogManager.GetLogger("Two", LoggerType.Grain);
            Assert.Equal(Severity.Verbose3,  target.SeverityLevel);  //  "Default severity level not calculated correctly for grain logger with an override"
        }

        /// <summary>
        ///A test for SetRuntimeLogLevel
        ///</summary>
        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_SetRuntimeLogLevelTest()
        {
            Logger target = LogManager.GetLogger("Test");
            Assert.Equal(Severity.Verbose,  target.SeverityLevel);  //  "Default severity level not calculated correctly for runtime logger with no overrides"
            LogManager.SetRuntimeLogLevel(Severity.Warning);
            Assert.Equal(Severity.Warning,  target.SeverityLevel);  //  "Default severity level not re-calculated correctly for runtime logger with no overrides"
        }

        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_SeverityLevelTest()
        {
            LoggerImpl target = LogManager.GetLogger("Test");
            Assert.Equal(Severity.Verbose,  target.SeverityLevel);  //  "Default severity level not calculated correctly for runtime logger with no overrides"
            target.SetSeverityLevel(Severity.Verbose2);
            Assert.Equal(Severity.Verbose2,  target.SeverityLevel);  //  "Custom severity level was not set properly"
            LogManager.SetRuntimeLogLevel(Severity.Warning);
            Assert.Equal(Severity.Verbose2,  target.SeverityLevel);  //  "Severity level was re-calculated even though a custom value had been set"
        }


        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_GetLoggerTest()
        {
            Logger logger = LogManager.GetLogger("GetLoggerTest", LoggerType.Runtime);
            Assert.NotNull(logger);

            Logger logger2 = LogManager.GetLogger("GetLoggerTest", LoggerType.Runtime);
            Assert.NotNull(logger2);
            Assert.True(ReferenceEquals(logger, logger2));

            Logger logger3 = LogManager.GetLogger("GetLoggerTest", LoggerType.Application);
            Assert.NotNull(logger3);
            Assert.True(ReferenceEquals(logger, logger3));
        }

        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_CreateMiniDump()
        {
            var dumpFile = LogManager.CreateMiniDump();
            output.WriteLine("Logger.CreateMiniDump dump file = " + dumpFile);
            Assert.NotNull(dumpFile);
            Assert.True(dumpFile.Exists, "Mini-dump file exists");
            Assert.True(dumpFile.Length > 0, "Mini-dump file has content");
            output.WriteLine("Logger.CreateMiniDump dump file location = " + dumpFile.FullName);
        }

        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_AddRemoveOverride()
        {
            const string name = "LoggerOverrideTest";
            const string fullName = "Runtime." + name;
            var logger = LogManager.GetLogger(name, LoggerType.Runtime);
            var initialLevel = logger.SeverityLevel;

            LogManager.AddTraceLevelOverride(fullName, Severity.Warning);
            Assert.Equal(Severity.Warning,  logger.SeverityLevel);  //  "Logger level not reset after override added"

            LogManager.RemoveTraceLevelOverride(fullName);
            Assert.Equal(initialLevel,  logger.SeverityLevel);  //  "Logger level not reset after override removed"
        }

        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_BulkMessageLimit_DifferentLoggers()
        {
            int n = 10000;
            LogManager.BulkMessageInterval = TimeSpan.FromMilliseconds(50);

            int logCode1 = 1;
            int logCode2 = 2;
            int expectedLogMessages1 = LogManager.BulkMessageLimit + 2 + 1;
            int expectedLogMessages2 = LogManager.BulkMessageLimit + 2;
            int expectedBulkLogMessages1 = 0;
            int expectedBulkLogMessages2 = 1;

            TestLogConsumer logConsumer = new TestLogConsumer(output);
            LogManager.LogConsumers.Add(logConsumer);
            Logger logger1 = LogManager.GetLogger("logger1");
            Logger logger2 = LogManager.GetLogger("logger2");

            // Write log messages to logger #1
            for (int i = 0; i < LogManager.BulkMessageLimit; i++)
            {
                logger1.Warn(logCode1, "Message " + (i + 1) + " to logger1");
            }

            // Write to logger#2 using same logCode -- This should NOT trigger the bulk message for logCode1
            logger2.Warn(logCode1, "Use same logCode to logger2");

            // Wait until the BulkMessageInterval time interval expires before writing the final log message - should cause any pending message flush);
            Thread.Sleep(LogManager.BulkMessageInterval);
            Thread.Sleep(1);

            // Write log messages to logger #2
            for (int i = 0; i < n; i++)
            {
                logger2.Warn(logCode2, "Message " + (i+1) + " to logger2" );
            }

            // Wait until the BulkMessageInterval time interval expires before writing the final log message - should cause any pending message flush);
            Thread.Sleep(LogManager.BulkMessageInterval);
            Thread.Sleep(1);

            logger1.Info(logCode1, "Penultimate message to logger1");
            logger2.Info(logCode2, "Penultimate message to logger2");

            logger1.Info(logCode1, "Final message to logger1");
            logger2.Info(logCode2, "Final message to logger2");

            Assert.Equal(expectedBulkLogMessages1,
                logConsumer.GetEntryCount(logCode1 + LogManager.BulkMessageSummaryOffset));
            Assert.Equal(expectedBulkLogMessages2,
                logConsumer.GetEntryCount(logCode2 + LogManager.BulkMessageSummaryOffset));
            Assert.Equal(expectedLogMessages1, logConsumer.GetEntryCount(logCode1));
            Assert.Equal(expectedLogMessages2, logConsumer.GetEntryCount(logCode2));
            Assert.Equal(0,  logConsumer.GetEntryCount(logCode1 - 1));  //  "Should not see any other entries -1"
            Assert.Equal(0,  logConsumer.GetEntryCount(logCode2 + 1));  //  "Should not see any other entries +1"
        }

        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_BulkMessageLimit_VerboseNotFiltered()
        {
            const string testName = "Logger_BulkMessageLimit_InfoNotFiltered";
            int n = 10000;
            LogManager.BulkMessageInterval = TimeSpan.FromMilliseconds(5);

            int logCode1 = 1;
            int expectedLogMessages1 = n + 2;
            int expectedBulkLogMessages1 = 0;

            TestLogConsumer logConsumer = new TestLogConsumer(output);
            LogManager.LogConsumers.Add(logConsumer);
            Logger logger1 = LogManager.GetLogger(testName);

            // Write log messages to logger #1
            for (int i = 0; i < n; i++)
            {
                logger1.Verbose(logCode1, "Verbose message " + (i + 1) + " to logger1");
            }

            // Wait until the BulkMessageInterval time interval expires before writing the final log message - should cause any pending message flush);
            Thread.Sleep(LogManager.BulkMessageInterval);
            Thread.Sleep(1);

            logger1.Info(logCode1, "Penultimate message to logger1");
            logger1.Info(logCode1, "Final message to logger1");

            Assert.Equal(expectedBulkLogMessages1,  logConsumer.GetEntryCount(logCode1 + LogManager.BulkMessageSummaryOffset));
            Assert.Equal(expectedLogMessages1, logConsumer.GetEntryCount(logCode1));
        }

        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_BulkMessageLimit_DifferentFinalLogCode()
        {
            const string testName = "Logger_BulkMessageLimit_DifferentFinalLogCode";
            int n = 10000;
            LogManager.BulkMessageInterval = TimeSpan.FromMilliseconds(50);

            int mainLogCode = TestUtils.Random.Next();
            int finalLogCode = mainLogCode + 10;
            int expectedMainLogMessages = LogManager.BulkMessageLimit;
            int expectedFinalLogMessages = 1;
            int expectedBulkLogMessages = 1;

            RunTestForLogFiltering(testName, n, mainLogCode, finalLogCode, expectedMainLogMessages, expectedFinalLogMessages, expectedBulkLogMessages);
        }

        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_BulkMessageLimit_SameFinalLogCode()
        {
            const string testName = "Logger_BulkMessageLimit_SameFinalLogCode";
            int n = 10000;
            LogManager.BulkMessageInterval = TimeSpan.FromMilliseconds(50);

            int mainLogCode = TestUtils.Random.Next(100000);
            int finalLogCode = mainLogCode; // Same as main code
            int expectedMainLogMessages = LogManager.BulkMessageLimit + 1; // == Loop + 1 final
            int expectedFinalLogMessages = expectedMainLogMessages;
            int expectedBulkLogMessages = 1;

            RunTestForLogFiltering(testName, n, mainLogCode, finalLogCode, expectedMainLogMessages,
                                   expectedFinalLogMessages, expectedBulkLogMessages);
        }

        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_BulkMessageLimit_Excludes_100000()
        {
            const string testName = "Logger_BulkMessageLimit_Excludes_100000";
            int n = 1000;
            LogManager.BulkMessageInterval = TimeSpan.FromMilliseconds(1);

            int mainLogCode = 100000;
            int finalLogCode = mainLogCode + 1;
            int expectedMainLogMessages = n;
            int expectedFinalLogMessages = 1;
            int expectedBulkLogMessages = 0;
            
            RunTestForLogFiltering(testName, n, finalLogCode, mainLogCode, expectedMainLogMessages, expectedFinalLogMessages, expectedBulkLogMessages);
        }

        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_MessageSizeLimit()
        {
            const string testName = "Logger_MessageSizeLimit";
            TestLogConsumer logConsumer = new TestLogConsumer(output);
            LogManager.LogConsumers.Add(logConsumer);
            Logger logger1 = LogManager.GetLogger(testName);

            StringBuilder sb = new StringBuilder();
            while (sb.Length <= LogManager.MAX_LOG_MESSAGE_SIZE)
            {
                sb.Append("1234567890");
            }
            string longString = sb.ToString();
            Assert.True(longString.Length > LogManager.MAX_LOG_MESSAGE_SIZE);

            int logCode1 = 1;

            logger1.Info(logCode1, longString);

            Assert.Equal(1,  logConsumer.GetEntryCount(logCode1));  //  "Should see original log message entry"
            Assert.Equal(1,  logConsumer.GetEntryCount((int)ErrorCode.Logger_LogMessageTruncated));  //  "Should also see 'Message truncated' message"
        }

        [Fact, TestCategory("Functional"), TestCategory("Logger")]
        public void Logger_Stats_MessageSizeLimit()
        {
            const string testName = "Logger_Stats_MessageSizeLimit";
            TestLogConsumer logConsumer = new TestLogConsumer(output);
            LogManager.LogConsumers.Add(logConsumer);
            Logger logger1 = LogManager.GetLogger(testName);

            const string StatsCounterBaseName = "LoggerTest.Stats.Size";

            var createdCounters = new List<string>();

            try
            {
                for (int i = 1; i <= 1000; i++)
                {
                    string name = StatsCounterBaseName + "." + i;
                    StatisticName counterName = new StatisticName(name);
                    CounterStatistic ctr = CounterStatistic.FindOrCreate(counterName);
                    ctr.IncrementBy(i);
                    createdCounters.Add(name);
                }

                LogStatistics statsLogger = new LogStatistics(TimeSpan.Zero, true, this.fixture.HostedCluster.SerializationManager);
                statsLogger.DumpCounters().Wait();

                int count = logConsumer.GetEntryCount((int)ErrorCode.PerfCounterDumpAll);
                output.WriteLine(count + " stats log message entries written");
                Assert.True(count > 1, "Should be some stats log message entries - saw " + count);
                Assert.Equal(0,  logConsumer.GetEntryCount((int)ErrorCode.Logger_LogMessageTruncated));  //  "Should not see any 'Message truncated' message"
            }
            finally
            {
                createdCounters.ForEach(name => CounterStatistic.Delete(name));
            }
        }

        [Fact, TestCategory("Logger"), TestCategory("Performance")]
        public void Logger_Perf_FileLogWriter()
        {
            const string testName = "Logger_Perf_FileLogWriter";
            TimeSpan target = TimeSpan.FromMilliseconds(1000);
            int n = 10000;
            int logCode = TestUtils.Random.Next(100000);

            var logFile = new FileInfo("log-" + testName + ".txt");
            ITraceTelemetryConsumer log = new FileTelemetryConsumer(logFile);
            RunLogWriterPerfTest(testName, n, logCode, target, log);
        }

        [Fact, TestCategory("Logger"), TestCategory("Performance")]
        public void Logger_Perf_TraceLogWriter()
        {
            const string testName = "Logger_Perf_TraceLogWriter";
            TimeSpan target = TimeSpan.FromMilliseconds(360);
            int n = 10000;
            int logCode = TestUtils.Random.Next(100000);

            ITraceTelemetryConsumer log = new TraceTelemetryConsumer();
            RunLogWriterPerfTest(testName, n, logCode, target, log);
        }

        [Fact, TestCategory("Logger"), TestCategory("Performance")]
        public void Logger_Perf_ConsoleLogWriter()
        {
            const string testName = "Logger_Perf_ConsoleLogWriter";
            TimeSpan target = TimeSpan.FromMilliseconds(100);
            int n = 10000;
            int logCode = TestUtils.Random.Next(100000);

            ITraceTelemetryConsumer log = new ConsoleTelemetryConsumer();
            RunLogWriterPerfTest(testName, n, logCode, target, log);
        }

        //[Fact, TestCategory("Logger"), TestCategory("Performance")]
        //public void Logger_Perf_EtwLogWriter()
        //{
        //    const string testName = "Logger_Perf_EtwLogWriter";
        //    TimeSpan target = TimeSpan.FromSeconds(0.7);
        //    int n = 100000;
        //
        //    ILogConsumer log = new LogWriterToEtw();
        //    RunLogWriterPerfTest(testName, n, target, log);
        //}

        [Fact, TestCategory("Logger"), TestCategory("Performance")]
        public void Logger_Perf_Logger_Console()
        {
            const string testName = "Logger_Perf_Logger_Console";
            TimeSpan target = TimeSpan.FromMilliseconds(50);
            int n = 10000;
            int logCode = TestUtils.Random.Next(100000);

            ITraceTelemetryConsumer logConsumer = new ConsoleTelemetryConsumer();
            LogManager.TelemetryConsumers.Add(logConsumer);
            LogManager.BulkMessageInterval = target;
            Logger logger = LogManager.GetLogger(testName);

            RunLoggerPerfTest(testName, n, logCode, target, logger);
        }

        [Fact, TestCategory("Logger"), TestCategory("Performance")]
        public void Logger_Perf_Logger_Dummy()
        {
            const string testName = "Logger_Perf_Logger_Dummy";
            TimeSpan target = TimeSpan.FromMilliseconds(30);
            int n = 10000;
            int logCode = TestUtils.Random.Next(100000);

            ILogConsumer logConsumer = new TestLogConsumer(output);
            LogManager.LogConsumers.Add(logConsumer);
            LogManager.BulkMessageInterval = target;
            Logger logger = LogManager.GetLogger(testName);

            RunLoggerPerfTest(testName, n, logCode, target, logger);
        }

        private void RunLogWriterPerfTest(string testName, int n, int logCode, TimeSpan target, ITraceTelemetryConsumer log)
        {
            // warm up
            log.TrackTrace(string.Format( "{0}|{1}|{2}|{3}", LoggerType.Runtime, testName, "msg warm up", logCode), Severity.Info);

            var messages = Enumerable.Range(0, n)
                .Select(i => string.Format("{0}|{1}|{2}|{3}", LoggerType.Runtime, testName, "msg " + i, logCode))
                .ToList();

            var stopwatch = Stopwatch.StartNew();
            foreach (string message in messages)
            {
                log.TrackTrace(message, Severity.Info);
            }
            stopwatch.Stop();
            var elapsed = stopwatch.Elapsed;
            output.WriteLine(testName + " : Elapsed time = " + elapsed);
            Assert.True(elapsed < target.Multiply(timingFactor), $"{testName}: Elapsed time {elapsed} exceeds target time {target}");
        }

        private void RunLoggerPerfTest(string testName, int n, int logCode, TimeSpan target, Logger logger)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            for (int i = 0; i < n; i++)
            {
                logger.Warn(logCode, "msg " + i);
            }
            var elapsed = stopwatch.Elapsed;
            string msg = testName + " : Elapsed time = " + elapsed;

            // Wait until the BulkMessageInterval time interval expires before wring the final log message - should cause bulk message flush
            while (stopwatch.Elapsed <= LogManager.BulkMessageInterval)
            {
                Thread.Sleep(10);
            }

            output.WriteLine(msg);
            logger.Info(logCode, msg);
            Assert.True(elapsed < target.Multiply(timingFactor), $"{testName}: Elapsed time {elapsed} exceeds target time {target}");
        }

        private void RunTestForLogFiltering(string testName, int n, int finalLogCode, int mainLogCode, int expectedMainLogMessages, int expectedFinalLogMessages, int expectedBulkLogMessages)
        {
            Assert.True(finalLogCode != (mainLogCode - 1), "Test code constraint -1");

            TestLogConsumer logConsumer = new TestLogConsumer(output);
            LogManager.LogConsumers.Add(logConsumer);
            Logger logger = LogManager.GetLogger(testName);

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            for (int i = 0; i < n; i++)
            {
                logger.Warn(mainLogCode, "msg " + i);
            }

            // Wait until the BulkMessageInterval time interval expires before wring the final log message - should cause bulk message flush);
            TimeSpan delay = LogManager.BulkMessageInterval - stopwatch.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                output.WriteLine("Sleeping for " + delay);
                Thread.Sleep(delay);
            }
            Thread.Sleep(10);

            logger.Info(finalLogCode, "Final msg");

            Assert.Equal(expectedMainLogMessages, logConsumer.GetEntryCount(mainLogCode));
            if (mainLogCode != finalLogCode)
            {
                Assert.Equal(expectedFinalLogMessages, logConsumer.GetEntryCount(finalLogCode));
            }
            Assert.Equal(expectedBulkLogMessages,
                logConsumer.GetEntryCount(mainLogCode + LogManager.BulkMessageSummaryOffset));
            Assert.Equal(0,  logConsumer.GetEntryCount(mainLogCode - 1));  //  "Should not see any other entries -1"
        }
    }

    class TestLogConsumer : ILogConsumer
    {
        private readonly ITestOutputHelper output;
        private readonly bool traceToOutput;

        public TestLogConsumer(ITestOutputHelper output, bool traceToOutput = false)
        {
            this.output = output;
            this.traceToOutput = traceToOutput;
        }

        private readonly Dictionary<int,int> entryCounts = new Dictionary<int, int>();
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
}
