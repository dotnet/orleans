using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Manages log sinks
    /// </summary>
    public class LogManager
    {
        /// <summary>
        /// Maximum length of log messages. 
        /// Log messages about this size will be truncated.
        /// </summary>
        public const int MAX_LOG_MESSAGE_SIZE = 20000;

        internal static string[] SeverityTable = { "OFF  ", "ERROR  ", "WARNING", "INFO   ", "VERBOSE ", "VERBOSE-2 ", "VERBOSE-3 " };

        private static Severity runtimeTraceLevel = Severity.Info;
        private static Severity appTraceLevel = Severity.Info;

        internal static IPEndPoint MyIPEndPoint { get; set; }

        /// <summary>
        /// Count limit for bulk message output.
        /// If the same log code is written more than <c>BulkMessageLimit</c> times in the <c>BulkMessageInterval</c> time period, 
        /// then only the first <c>BulkMessageLimit</c> individual messages will be written, plus a count of how bulk messages suppressed.
        /// </summary>
        public static int BulkMessageLimit { get; set; }

        /// <summary>
        /// Time limit for bulk message output.
        /// If the same log code is written more than <c>BulkMessageLimit</c> times in the <c>BulkMessageInterval</c> time period, 
        /// then only the first <c>BulkMessageLimit</c> individual messages will be written, plus a count of how bulk messages suppressed.
        /// </summary>
        public static TimeSpan BulkMessageInterval { get; set; }

        internal const int BulkMessageSummaryOffset = 500000;

        /// <summary>
        /// The set of <see cref="ILogConsumer"/> references to write log events to. 
        /// </summary>
        public static ConcurrentBag<ILogConsumer> LogConsumers { get; private set; }

        /// <summary>
        /// The set of <see cref="ITelemetryConsumer"/> references to write telemetry events to. 
        /// </summary>
        public static ConcurrentBag<ITelemetryConsumer> TelemetryConsumers { get; private set; }
        
        /// <summary>
        /// Flag to suppress output of dates in log messages during unit test runs
        /// </summary>
        internal static bool ShowDate = true;

        // TODO: This is a hack (global variable) to work around initialization order issues in telemetry provider code.
        // This is used by Performance Counter code to know which grains to create counters for.
        internal static IList<string> GrainTypes = null;

        // http://www.csharp-examples.net/string-format-datetime/
        // http://msdn.microsoft.com/en-us/library/system.globalization.datetimeformatinfo.aspx

        internal static int defaultModificationCounter;
        internal static readonly object lockable;

        internal static readonly List<Tuple<string, Severity>> traceLevelOverrides = new List<Tuple<string, Severity>>();

        private const int LOGGER_INTERN_CACHE_INITIAL_SIZE = InternerConstants.SIZE_MEDIUM;
        private static readonly TimeSpan loggerInternCacheCleanupInterval = InternerConstants.DefaultCacheCleanupFreq;
        private static Interner<string, LoggerImpl> loggerStoreInternCache;

        private static readonly TimeSpan defaultBulkMessageInterval = TimeSpan.FromMinutes(1);

        /// <summary>List of log codes that won't have bulk message compaction policy applied to them</summary>
        internal static readonly int[] excludedBulkLogCodes = {
            0,
            (int)ErrorCode.Runtime
        };


        static LogManager()
        {
            defaultModificationCounter = 0;
            lockable = new object();
            LogConsumers = new ConcurrentBag<ILogConsumer>();
            TelemetryConsumers = new ConcurrentBag<ITelemetryConsumer>();
            BulkMessageInterval = defaultBulkMessageInterval;
            BulkMessageLimit = Constants.DEFAULT_LOGGER_BULK_MESSAGE_LIMIT;
        }

        /// <summary>
        /// Whether the Orleans Logger infrastructure has been previously initialized.
        /// </summary>
        public static bool IsInitialized { get; private set; }

#pragma warning disable 1574
        /// <summary>
        /// Initialize the Orleans Logger subsystem in this process / app domain with the specified configuration settings.
        /// </summary>
        /// <remarks>
        /// In most cases, this call will be made automatically at the approproate poine by the Orleans runtime 
        /// -- must commonly during silo initialization and/or client runtime initialization.
        /// </remarks>
        /// <seealso cref="GrainClient.Initialize()"/>
        /// <seealso cref="Orleans.Host.Azure.Client.AzureClient.Initialize()"/>
        /// <param name="config">Configuration settings to be used for initializing the Logger susbystem state.</param>
        /// <param name="configChange">Indicates an update to existing config settings.</param>
#pragma warning restore 1574
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public static void Initialize(ITraceConfiguration config, bool configChange = false)
        {
            if (config == null) throw new ArgumentNullException("config", "No logger config data provided.");

            lock (lockable)
            {
                if (IsInitialized && !configChange) return; // Already initialized

                loggerStoreInternCache = new Interner<string, LoggerImpl>(LOGGER_INTERN_CACHE_INITIAL_SIZE, loggerInternCacheCleanupInterval);

                BulkMessageLimit = config.BulkMessageLimit;
                runtimeTraceLevel = config.DefaultTraceLevel;
                appTraceLevel = config.DefaultTraceLevel;
                SetTraceLevelOverrides(config.TraceLevelOverrides);
                Message.LargeMessageSizeThreshold = config.LargeMessageWarningThreshold;
                SerializationManager.LARGE_OBJECT_LIMIT = config.LargeMessageWarningThreshold;
                RequestContext.PropagateActivityId = config.PropagateActivityId;
                defaultModificationCounter++;
                if (configChange)
                    return; // code below should only apply during creation

                /*
                 * Encountered a null reference exception during load testing using the test adapter
                 * The issue appears to be related to the closing of the trace listeners, commenting out the following lines appears to "fix" the issue
                 * R.E. 10/12/2010
                 
                foreach (TraceListener l in Trace.Listeners)
                {
                    l.Close();
                }
                Trace.Listeners.Clear();
                
                // We need the default listener so that Debug.Assert and Debug.Fail work properly
                Trace.Listeners.Add(new DefaultTraceListener());
                */
                if (config.TraceToConsole)
                {
                    if (!TelemetryConsumers.OfType<ConsoleTelemetryConsumer>().Any())
                    {
                        TelemetryConsumers.Add(new ConsoleTelemetryConsumer());
                    }
                }
                if (!string.IsNullOrEmpty(config.TraceFileName))
                {
                    try
                    {
                        if (!TelemetryConsumers.OfType<FileTelemetryConsumer>().Any())
                        {
                            TelemetryConsumers.Add(new FileTelemetryConsumer(config.TraceFileName));
                        }
                    }
                    catch (Exception exc)
                    {
                        Trace.Listeners.Add(new DefaultTraceListener());
                        Trace.TraceError("Error opening trace file {0} -- Using DefaultTraceListener instead -- Exception={1}", config.TraceFileName, exc);
                    }
                }

                if (Trace.Listeners.Count > 0)
                {
                    if (!TelemetryConsumers.OfType<TraceTelemetryConsumer>().Any())
                    {
                        TelemetryConsumers.Add(new TraceTelemetryConsumer());
                    }
                }

                IsInitialized = true;
            }
        }

        /// <summary>
        /// Uninitialize the Orleans Logger subsystem in this process / app domain.
        /// </summary>
        public static void UnInitialize()
        {
            lock (lockable)
            {
                Close();
                LogConsumers = new ConcurrentBag<ILogConsumer>();
                TelemetryConsumers = new ConcurrentBag<ITelemetryConsumer>();

                loggerStoreInternCache?.StopAndClear();

                BulkMessageInterval = defaultBulkMessageInterval;
                BulkMessageLimit = Constants.DEFAULT_LOGGER_BULK_MESSAGE_LIMIT;
                IsInitialized = false;
            }
        }

        internal static Severity GetDefaultSeverityForLog(string source, LoggerType logType)
        {
            string expandedName = logType + "." + source;

            lock (lockable)
            {
                if (traceLevelOverrides.Count > 0)
                {
                    foreach (var o in traceLevelOverrides)
                    {
                        if (expandedName.StartsWith(o.Item1))
                        {
                            return o.Item2;
                        }
                    }
                }
            }

            return logType == LoggerType.Runtime ? runtimeTraceLevel : appTraceLevel;
        }

        /// <summary>
        /// Find the Logger with the specified name
        /// </summary>
        /// <param name="loggerName">Name of the Logger to find</param>
        /// <returns>Logger associated with the specified name</returns>
        internal static LoggerImpl FindLogger(string loggerName)
        {
            if (loggerStoreInternCache == null) return null;

            LoggerImpl logger;
            loggerStoreInternCache.TryFind(loggerName, out logger);
            return logger;
        }

        /// <summary>
        /// Find existing or create new Logger with the specified name
        /// </summary>
        /// <param name="loggerName">Name of the Logger to find</param>
        /// <param name="logType">Type of Logger, if it needs to be created</param>
        /// <returns>Logger associated with the specified name</returns>
        internal static LoggerImpl GetLogger(string loggerName, LoggerType logType)
        {
            return loggerStoreInternCache != null ?
                loggerStoreInternCache.FindOrCreate(loggerName, name => new LoggerImpl(name, logType)) :
                new LoggerImpl(loggerName, logType);
        }

        internal static LoggerImpl GetLogger(string loggerName)
        {
            return GetLogger(loggerName, LoggerType.Runtime);
        }

        /// <summary>
        /// Set the default log level of all Runtime Loggers.
        /// </summary>
        /// <param name="severity">The new log level to use</param>
        public static void SetRuntimeLogLevel(Severity severity)
        {
            runtimeTraceLevel = severity;
            defaultModificationCounter++;
        }

        /// <summary>
        /// Set the default log level of all Grain and Application Loggers.
        /// </summary>
        /// <param name="severity">The new log level to use</param>
        public static void SetAppLogLevel(Severity severity)
        {
            appTraceLevel = severity;
            defaultModificationCounter++;
        }

        /// <summary>
        /// Set new trace level overrides for particular loggers, beyond the default log levels.
        /// Any previous trace levels for particular Logger's will be discarded.
        /// </summary>
        /// <param name="overrides">The new set of log level overrided to use.</param>
        public static void SetTraceLevelOverrides(IList<Tuple<string, Severity>> overrides)
        {
            List<LoggerImpl> loggers;
            lock (lockable)
            {
                traceLevelOverrides.Clear();
                traceLevelOverrides.AddRange(overrides);
                if (traceLevelOverrides.Count > 0)
                {
                    traceLevelOverrides.Sort(new TraceOverrideComparer());
                }
                defaultModificationCounter++;
                loggers = loggerStoreInternCache != null ? loggerStoreInternCache.AllValues() : new List<LoggerImpl>();
            }
            foreach (var logger in loggers)
            {
                logger.CheckForSeverityOverride();
            }
        }

        /// <summary>
        /// Add a new trace level override for a particular logger, beyond the default log levels.
        /// Any previous trace levels for other Logger's will not be changed.
        /// </summary>
        /// <param name="prefix">The logger names (with prefix matching) that this new log level should apply to.</param>
        /// <param name="level">The new log level to use for this logger.</param>
        public static void AddTraceLevelOverride(string prefix, Severity level)
        {
            List<LoggerImpl> loggers;
            lock (lockable)
            {
                traceLevelOverrides.Add(new Tuple<string, Severity>(prefix, level));
                if (traceLevelOverrides.Count > 0)
                {
                    traceLevelOverrides.Sort(new TraceOverrideComparer());
                }
                loggers = loggerStoreInternCache != null ? loggerStoreInternCache.AllValues() : new List<LoggerImpl>();
            }
            foreach (var logger in loggers)
            {
                logger.CheckForSeverityOverride();
            }
        }

        /// <summary>
        /// Remove a new trace level override for a particular logger.
        /// The log level for that logger will revert to the current global default setings.
        /// Any previous trace levels for other Logger's will not be changed.
        /// </summary>
        /// <param name="prefix">The logger names (with prefix matching) that this new log level change should apply to.</param>
        public static void RemoveTraceLevelOverride(string prefix)
        {
            List<LoggerImpl> loggers;
            lock (lockable)
            {
                var newOverrides = traceLevelOverrides.Where(tuple => !tuple.Item1.Equals(prefix)).ToList();
                traceLevelOverrides.Clear();
                traceLevelOverrides.AddRange(newOverrides);
                if (traceLevelOverrides.Count > 0)
                {
                    traceLevelOverrides.Sort(new TraceOverrideComparer());
                }
                loggers = loggerStoreInternCache != null ? loggerStoreInternCache.AllValues() : new List<LoggerImpl>();
            }
            foreach (var logger in loggers)
            {
                if (!logger.MatchesPrefix(prefix)) continue;
                if (logger.CheckForSeverityOverride()) continue;

                logger.useCustomSeverityLevel = false;
                logger.defaultCopiedCounter = 0;
                logger.severity = GetDefaultSeverityForLog(logger.Name, logger.loggerType);
            }
        }

        /// <summary>
        /// Attempt to flush any pending trace log writes to disk / backing store
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        internal static void Flush()
        {
            try
            {
                // Flush trace logs to disk
                Trace.Flush();

                foreach (IFlushableLogConsumer consumer in LogConsumers.OfType<IFlushableLogConsumer>())
                {
                    try
                    {
                        consumer.Flush();
                    }
                    catch (Exception) { }
                }

                foreach (var consumer in TelemetryConsumers)
                {
                    try
                    {
                        consumer.Flush();
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        internal static void Close()
        {
            Flush();
            try
            {
                foreach (ICloseableLogConsumer consumer in LogConsumers.OfType<ICloseableLogConsumer>())
                {
                    try
                    {
                        consumer.Close();
                    }
                    catch (Exception) { }
                }

                foreach (var consumer in TelemetryConsumers)
                {
                    try
                    {
                        consumer.Close();
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Create a mini-dump file for the current state of this process
        /// </summary>
        /// <param name="dumpType">Type of mini-dump to create</param>
        /// <returns><c>FileInfo</c> for the location of the newly created mini-dump file</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.Runtime.InteropServices.SafeHandle.DangerousGetHandle")]
        internal static FileInfo CreateMiniDump(MiniDumpType dumpType = MiniDumpType.MiniDumpNormal)
        {
            const string dateFormat = "yyyy-MM-dd-HH-mm-ss-fffZ"; // Example: 2010-09-02-09-50-43-341Z

            var thisAssembly = Assembly.GetEntryAssembly()
#if !NETSTANDARD
                ?? Assembly.GetCallingAssembly()
#endif
                ?? typeof(LogManager)
                .GetTypeInfo().Assembly;

            var dumpFileName = $@"{thisAssembly.GetName().Name}-MiniDump-{DateTime.UtcNow.ToString(dateFormat,
                    CultureInfo.InvariantCulture)}.dmp";

            using (var stream = File.Create(dumpFileName))
            {
                var process = Process.GetCurrentProcess();

                // It is safe to call DangerousGetHandle() here because the process is already crashing.
                var handle = GetProcessHandle(process);
                NativeMethods.MiniDumpWriteDump(
                    handle,
                    process.Id,
                    stream.SafeFileHandle.DangerousGetHandle(),
                    dumpType,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }

            return new FileInfo(dumpFileName);
        }

        private static IntPtr GetProcessHandle(Process process)
        {
#if NETSTANDARD
            return process.SafeHandle.DangerousGetHandle();
#else
            return process.Handle;
#endif
        }

        /// <summary>
        /// This custom comparer lets us sort the TraceLevelOverrides list so that the longest prefix comes first
        /// </summary>
        private class TraceOverrideComparer : Comparer<Tuple<string, Severity>>
        {
            public override int Compare(Tuple<string, Severity> x, Tuple<string, Severity> y)
            {
                return y.Item1.Length.CompareTo(x.Item1.Length);
            }
        }

        private static class NativeMethods
        {
            [DllImport("Dbghelp.dll")]
            public static extern bool MiniDumpWriteDump(
                IntPtr hProcess,
                int processId,
                IntPtr hFile,
                MiniDumpType dumpType,
                IntPtr exceptionParam,
                IntPtr userStreamParam,
                IntPtr callbackParam);
        }
    }

    internal enum MiniDumpType
    {
        // ReSharper disable UnusedMember.Global
        MiniDumpNormal = 0x00000000,
        MiniDumpWithDataSegs = 0x00000001,
        MiniDumpWithFullMemory = 0x00000002,
        MiniDumpWithHandleData = 0x00000004,
        MiniDumpFilterMemory = 0x00000008,
        MiniDumpScanMemory = 0x00000010,
        MiniDumpWithUnloadedModules = 0x00000020,
        MiniDumpWithIndirectlyReferencedMemory = 0x00000040,
        MiniDumpFilterModulePaths = 0x00000080,
        MiniDumpWithProcessThreadData = 0x00000100,
        MiniDumpWithPrivateReadWriteMemory = 0x00000200,
        MiniDumpWithoutOptionalData = 0x00000400,
        MiniDumpWithFullMemoryInfo = 0x00000800,
        MiniDumpWithThreadInfo = 0x00001000,
        MiniDumpWithCodeSegs = 0x00002000,
        MiniDumpWithoutManagedState = 0x00004000,
        // ReSharper restore UnusedMember.Global
    }
}
