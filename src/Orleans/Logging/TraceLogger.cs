/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
﻿using Microsoft.WindowsAzure.Storage;
﻿using Orleans.Runtime.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// The TraceLogger class is a convenient wrapper around the .Net Trace class.
    /// It provides more flexible configuration than the Trace class.
    /// </summary>
    public class TraceLogger : Logger
    {
        /// <summary>
        /// The TraceLogger class distinguishes between three categories of loggers:
        /// <list type="table"><listheader><term>Value</term><description>Description</description></listheader>
        /// <item>
        /// <term>Runtime</term>
        /// <description>Logs that are written by the Orleans run-time itself.
        /// This category should not be used by application code.</description>
        /// </item>
        /// <item>
        /// <term>Grain</term>
        /// <description>Logs that are written by application grains.
        /// This category should be used by code that runs as Orleans grains in a silo.</description>
        /// </item>
        /// <item>
        /// <term>Application</term>
        /// <description>Logs that are written by the client application.
        /// This category should be used by client-side application code.</description>
        /// </item>
        /// </list>
        /// </summary>
        public enum LoggerType
        {
            Runtime,
            Grain,
            Application,
            Provider
        }

        /// <summary>
        /// Maximum length of log messages. 
        /// Log messages about this size will be truncated.
        /// </summary>
        public const int MAX_LOG_MESSAGE_SIZE = 20000;

        internal static string[] SeverityTable = { "OFF  ", "ERROR  ", "WARNING", "INFO   ", "VERBOSE ", "VERBOSE-2 ", "VERBOSE-3 " };

        private static Severity runtimeTraceLevel = Severity.Info;
        private static Severity appTraceLevel = Severity.Info;
        private static FileInfo logOutputFile;

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
        /// If any .NET trace listeners are defined in app.config, then <see cref="LogWriterToTrace"/> 
        /// is automatically added to this list to forward the Orleans log output to those trace listeners.
        /// </summary>
        public static ConcurrentBag<ILogConsumer> LogConsumers { get; private set; }

        /// <summary>
        /// Flag to suppress output of dates in log messages during unit test runs
        /// </summary>
        internal static bool ShowDate = true;

        // http://www.csharp-examples.net/string-format-datetime/
        // http://msdn.microsoft.com/en-us/library/system.globalization.datetimeformatinfo.aspx
        private const string TIME_FORMAT = "HH:mm:ss.fff 'GMT'"; // Example: 09:50:43.341 GMT
        private const string DATE_FORMAT = "yyyy-MM-dd " + TIME_FORMAT; // Example: 2010-09-02 09:50:43.341 GMT - Variant of UniversalSorta­bleDateTimePat­tern

        private static int defaultModificationCounter;
        private int defaultCopiedCounter;
        private Severity severity;
        private bool useCustomSeverityLevel = false;

        private readonly LoggerType loggerType;
        private readonly string logName;
        private static readonly object lockable;

        private static readonly List<Tuple<string, Severity>> traceLevelOverrides = new List<Tuple<string, Severity>>();

        private const int LOGGER_INTERN_CACHE_INITIAL_SIZE = InternerConstants.SIZE_MEDIUM;
        private static readonly TimeSpan loggerInternCacheCleanupInterval = InternerConstants.DefaultCacheCleanupFreq;
        private static Interner<string, TraceLogger> loggerStoreInternCache;

        private static readonly TimeSpan defaultBulkMessageInterval = TimeSpan.FromMinutes(1);

        private Dictionary<int, int> recentLogMessageCounts = new Dictionary<int, int>();
        private DateTime lastBulkLogMessageFlush = DateTime.MinValue;

        /// <summary>List of log codes that won't have bulk message compaction policy applied to them</summary>
        private static readonly int[] excludedBulkLogCodes = {
            0,
            (int)ErrorCode.Runtime
        };

        /// <summary>
        /// The current severity level for this TraceLogger.
        /// Log entries will be written if their severity is (logically) equal to or greater than this level.
        /// If it is not explicitly set, then a default value will be calculated based on the logger's type and name.
        /// Note that changes to the global default settings will be propagated to existing loggers that are using the default severity.
        /// </summary>
        public override Severity SeverityLevel
        {
            get
            {
                if (useCustomSeverityLevel || (defaultCopiedCounter >= defaultModificationCounter)) return severity;

                severity = GetDefaultSeverityForLog(logName, loggerType);
                defaultCopiedCounter = defaultModificationCounter;
                return severity;
            }
        }

        /// <summary>
        /// Set a new severity level for this TraceLogger.
        /// Log entries will be written if their severity is (logically) equal to or greater than this level.
        /// </summary>
        /// <param name="sev">New severity level to be used for filtering log messages.</param>
        public void SetSeverityLevel(Severity sev)
        {
            severity = sev;
            useCustomSeverityLevel = true;
        }

        static TraceLogger()
        {
            defaultModificationCounter = 0;
            lockable = new object();
            LogConsumers = new ConcurrentBag<ILogConsumer>();
            BulkMessageInterval = defaultBulkMessageInterval;
            BulkMessageLimit = Constants.DEFAULT_LOGGER_BULK_MESSAGE_LIMIT;
        }

        /// <summary>
        /// Constructs a TraceLogger with the given name and type.
        /// </summary>
        /// <param name="source">The name of the source of log entries for this TraceLogger.
        /// Typically this is the full name of the class that is using this TraceLogger.</param>
        /// <param name="logType">The category of TraceLogger to create.</param>
        private TraceLogger(string source, LoggerType logType)
        {
            defaultCopiedCounter = -1;
            logName = source;
            loggerType = logType;
            useCustomSeverityLevel = CheckForSeverityOverride();
        }

        /// <summary>
        /// Whether the Orleans TraceLogger infrastructure has been previously initialized.
        /// </summary>
        public static bool IsInitialized { get; private set; }

        #pragma warning disable 1574
        /// <summary>
        /// Initialize the Orleans TraceLogger subsystem in this process / app domain with the specified configuration settings.
        /// </summary>
        /// <remarks>
        /// In most cases, this call will be made automatically at the approproate poine by the Orleans runtime 
        /// -- must commonly during silo initialization and/or client runtime initialization.
        /// </remarks>
        /// <seealso cref="GrainClient.Initialize()"/>
        /// <seealso cref="Orleans.Host.Azure.Client.AzureClient.Initialize()"/>
        /// <param name="config">Configuration settings to be used for initializing the TraceLogger susbystem state.</param>
        #pragma warning restore 1574
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public static void Initialize(ITraceConfiguration config, bool configChange = false)
        {
            lock (lockable)
            {
                if (IsInitialized && !configChange) return; // Already initialized

                loggerStoreInternCache = new Interner<string, TraceLogger>(LOGGER_INTERN_CACHE_INITIAL_SIZE, loggerInternCacheCleanupInterval);

                BulkMessageLimit = config.BulkMessageLimit;
                runtimeTraceLevel = config.DefaultTraceLevel;
                appTraceLevel = config.DefaultTraceLevel;
                SetTraceLevelOverrides(config.TraceLevelOverrides);
                Message.WriteMessagingTraces = config.WriteMessagingTraces;
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
                    bool containsConsoleListener = false;
                    foreach (TraceListener l in Trace.Listeners)
                    {
                        if (l.GetType() != typeof (ConsoleTraceListener)) continue;

                        containsConsoleListener = true;
                        break;
                    }
                    if (!containsConsoleListener)
                    {
                        LogConsumers.Add(new LogWriterToConsole());
                    }
                }
                if (!string.IsNullOrEmpty(config.TraceFileName))
                {
                    try
                    {
                        logOutputFile = new FileInfo(config.TraceFileName);
                        var l = new LogWriterToFile(logOutputFile);
                        LogConsumers.Add(l);
                    }
                    catch (Exception exc)
                    {
                        Trace.Listeners.Add(new DefaultTraceListener());
                        Trace.TraceError("Error opening trace file {0} -- Using DefaultTraceListener instead -- Exception={1}", logOutputFile, exc);
                    }
                }

                if (Trace.Listeners.Count > 0)
                {
                    // Plumb in log consumer to write to Trace listeners
                    var traceLogConsumer = new LogWriterToTrace();
                    LogConsumers.Add(traceLogConsumer);
                }

                IsInitialized = true;
            }
        }

        /// <summary>
        /// Uninitialize the Orleans TraceLogger subsystem in this process / app domain.
        /// </summary>
        public static void UnInitialize()
        {
            lock (lockable)
            {
                Close();
                LogConsumers = new ConcurrentBag<ILogConsumer>();
                if (loggerStoreInternCache != null) loggerStoreInternCache.StopAndClear();
                BulkMessageInterval = defaultBulkMessageInterval;
                BulkMessageLimit = Constants.DEFAULT_LOGGER_BULK_MESSAGE_LIMIT;
                IsInitialized = false;
            }
        }

        private static Severity GetDefaultSeverityForLog(string source, LoggerType logType)
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

        private bool MatchesPrefix(string prefix)
        {
            return logName.StartsWith(prefix, StringComparison.Ordinal)
                || (loggerType + "." + logName).StartsWith(prefix, StringComparison.Ordinal);
        }

        private bool CheckForSeverityOverride()
        {
            lock (lockable)
            {
                if (traceLevelOverrides.Count <= 0) return false;

                foreach (var o in traceLevelOverrides)
                {
                    if (!MatchesPrefix(o.Item1)) continue;

                    severity = o.Item2;
                    useCustomSeverityLevel = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Find the TraceLogger with the specified name
        /// </summary>
        /// <param name="loggerName">Name of the TraceLogger to find</param>
        /// <returns>TraceLogger associated with the specified name</returns>
        internal static TraceLogger FindLogger(string loggerName)
        {
            if (loggerStoreInternCache == null) return null;

            TraceLogger logger;
            loggerStoreInternCache.TryFind(loggerName, out logger);
            return logger;
        }

        /// <summary>
        /// Find existing or create new TraceLogger with the specified name
        /// </summary>
        /// <param name="loggerName">Name of the TraceLogger to find</param>
        /// <param name="logType">Type of TraceLogger, if it needs to be created</param>
        /// <returns>TraceLogger associated with the specified name</returns>
        internal static TraceLogger GetLogger(string loggerName, LoggerType logType)
        {
            return loggerStoreInternCache != null ? 
                loggerStoreInternCache.FindOrCreate(loggerName, () => new TraceLogger(loggerName, logType)) : 
                new TraceLogger(loggerName, logType);
        }

        internal static TraceLogger GetLogger(string loggerName)
        {
            return GetLogger(loggerName, LoggerType.Runtime);
        }

        /// <summary>
        /// Find the log file associated with the specified TraceLogger
        /// </summary>
        /// <param name="loggerName">Name of the TraceLogger to find the log file for</param>
        /// <returns>File info for the associated log file</returns>
        internal static FileInfo GetLogFile(string loggerName)
        {
            return logOutputFile;
        }

        /// <summary>
        /// Search the specified log according to the 
        /// </summary>
        /// <param name="logName"></param>
        /// <param name="searchFrom"></param>
        /// <param name="searchTo"></param>
        /// <param name="searchPattern"></param>
        /// <returns></returns>
        internal static string[] SearchLogFile(string logName, DateTime searchFrom, DateTime searchTo, Regex searchPattern)
        {
            FileInfo file = GetLogFile(logName);
            string logText;
            using (var f = new StreamReader(file.FullName))
            {
                logText = f.ReadToEnd();
            }

            // Perform regex search
            MatchCollection matches = searchPattern.Matches(logText);
            var matchOutput = new string[matches.Count];
            int i = 0;
            foreach (Match m in matches)
            {
                matchOutput[i++] = m.Value;
            }

            return matchOutput;
        }

        /// <summary>
        /// Set the default log level of all Runtime Loggers.
        /// </summary>
        /// <param name="newTraceLevel">The new log level to use</param>
        public static void SetRuntimeLogLevel(Severity severity)
        {
            runtimeTraceLevel = severity;
            defaultModificationCounter++;
        }

        /// <summary>
        /// Set the default log level of all Grain and Application Loggers.
        /// </summary>
        /// <param name="newTraceLevel">The new log level to use</param>
        public static void SetAppLogLevel(Severity severity)
        {
            appTraceLevel = severity;
            defaultModificationCounter++;
        }

        /// <summary>
        /// Set new trace level overrides for particular loggers, beyond the default log levels.
        /// Any previous trace levels for particular TraceLogger's will be discarded.
        /// </summary>
        /// <param name="overrides">The new set of log level overrided to use.</param>
        public static void SetTraceLevelOverrides(IList<Tuple<string, Severity>> overrides)
        {
            List<TraceLogger> loggers;
            lock (lockable)
            {
                traceLevelOverrides.Clear();
                traceLevelOverrides.AddRange(overrides);
                if (traceLevelOverrides.Count > 0)
                {
                    traceLevelOverrides.Sort(new TraceOverrideComparer());
                }
                defaultModificationCounter++;
                loggers = loggerStoreInternCache != null ? loggerStoreInternCache.AllValues() : new List<TraceLogger>();
            }
            foreach (var logger in loggers)
            {
                logger.CheckForSeverityOverride();
            }
        }

        /// <summary>
        /// Add a new trace level override for a particular logger, beyond the default log levels.
        /// Any previous trace levels for other TraceLogger's will not be changed.
        /// </summary>
        /// <param name="prefix">The logger names (with prefix matching) that this new log level should apply to.</param>
        /// <param name="level">The new log level to use for this logger.</param>
        public static void AddTraceLevelOverride(string prefix, Severity level)
        {
            List<TraceLogger> loggers;
            lock (lockable)
            {
                traceLevelOverrides.Add(new Tuple<string, Severity>(prefix, level));
                if (traceLevelOverrides.Count > 0)
                {
                    traceLevelOverrides.Sort(new TraceOverrideComparer());
                }
                loggers = loggerStoreInternCache != null ? loggerStoreInternCache.AllValues() : new List<TraceLogger>();
            }
            foreach (var logger in loggers)
            {
                logger.CheckForSeverityOverride();
            }
        }

        /// <summary>
        /// Remove a new trace level override for a particular logger.
        /// The log level for that logger will revert to the current global default setings.
        /// Any previous trace levels for other TraceLogger's will not be changed.
        /// </summary>
        /// <param name="prefix">The logger names (with prefix matching) that this new log level change should apply to.</param>
        public static void RemoveTraceLevelOverride(string prefix)
        {
            List<TraceLogger> loggers;
            lock (lockable)
            {
                var newOverrides = traceLevelOverrides.Where(tuple => !tuple.Item1.Equals(prefix)).ToList();
                traceLevelOverrides.Clear();
                traceLevelOverrides.AddRange(newOverrides);
                if (traceLevelOverrides.Count > 0)
                {
                    traceLevelOverrides.Sort(new TraceOverrideComparer());
                }
                loggers = loggerStoreInternCache != null ? loggerStoreInternCache.AllValues() : new List<TraceLogger>();
            }
            foreach (var logger in loggers)
            {
                if (!logger.MatchesPrefix(prefix)) continue;
                if (logger.CheckForSeverityOverride()) continue;

                logger.useCustomSeverityLevel = false;
                logger.defaultCopiedCounter = 0;
                logger.severity = GetDefaultSeverityForLog(logger.logName, logger.loggerType);
            }
        }

        //----------------------

        /// <summary>
        /// Writes a log entry at the Verbose severity level.
        /// Verbose is suitable for debugging information that should usually not be logged in production.
        /// Verbose is lower than Info.
        /// </summary>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public override void Verbose(string format, params object[] args)
        {
            Log(0, Severity.Verbose, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose2 severity level.
        /// Verbose2 is lower than Verbose.
        /// </summary>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public override void Verbose2(string format, params object[] args)
        {
            Log(0, Severity.Verbose2, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose3 severity level.
        /// Verbose3 is the lowest severity level.
        /// </summary>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public override void Verbose3(string format, params object[] args)
        {
            Log(0, Severity.Verbose3, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Info severity level.
        /// Info is suitable for information that does not indicate an error but that should usually be logged in production.
        /// Info is lower than Warning.
        /// </summary>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        ////[Obsolete("Use method Info(logCode,format,args) instead")]
        public override void Info(string format, params object[] args)
        {
            Log(0, Severity.Info, format, args, null);
        }

        #region Public log methods using int LogCode categorization.

        /// <summary>
        /// Writes a log entry at the Verbose severity level, with the specified log id code.
        /// Verbose is suitable for debugging information that should usually not be logged in production.
        /// Verbose is lower than Info.
        /// </summary>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public override void Verbose(int logCode, string format, params object[] args)
        {
            Log(logCode, Severity.Verbose, format, args, null);
        }
        /// <summary>
        /// Writes a log entry at the Verbose2 severity level, with the specified log id code.
        /// Verbose2 is lower than Verbose.
        /// </summary>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public override void Verbose2(int logCode, string format, params object[] args)
        {
            Log(logCode, Severity.Verbose2, format, args, null);
        }
        /// <summary>
        /// Writes a log entry at the Verbose3 severity level, with the specified log id code.
        /// Verbose3 is the lowest severity level.
        /// </summary>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public override void Verbose3(int logCode, string format, params object[] args)
        {
            Log(logCode, Severity.Verbose3, format, args, null);
        }
        /// <summary>
        /// Writes a log entry at the Info severity level, with the specified log id code.
        /// Info is suitable for information that does not indicate an error but that should usually be logged in production.
        /// Info is lower than Warning.
        /// </summary>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public override void Info(int logCode, string format, params object[] args)
        {
            Log(logCode, Severity.Info, format, args, null);
        }
        /// <summary>
        /// Writes a log entry at the Warning severity level, with the specified log id code.
        /// Warning is suitable for problem conditions that the system or application can handle by itself,
        /// but that the administrator should be aware of.
        /// Typically these are situations that are expected but that may eventually require an administrative
        /// response if they recur.
        /// Warning is lower than Error.
        /// </summary>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public override void Warn(int logCode, string format, params object[] args)
        {
            Log(logCode, Severity.Warning, format, args, null);
        }
        /// <summary>
        /// Writes a log entry at the Warning severity level, with the specified log id code.
        /// Warning is suitable for problem conditions that the system or application can handle by itself,
        /// but that the administrator should be aware of.
        /// Typically these are situations that are expected but that may eventually require an administrative
        /// response if they recur.
        /// Warning is lower than Error.
        /// </summary>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The warning message to log.</param>
        /// <param name="exception">An exception related to the warning, if any.</param>
        public override void Warn(int logCode, string message, Exception exception)
        {
            Log(logCode, Severity.Warning, message, new object[] { }, exception);
        }
        /// <summary>
        /// Writes a log entry at the Error severity level, with the specified log id code.
        /// Error is suitable for problem conditions that require immediate administrative response.
        /// </summary>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The error message to log.</param>
        /// <param name="exception">An exception related to the error, if any.</param>
        public override void Error(int logCode, string message, Exception exception = null)
        {
            Log(logCode, Severity.Error, message, new object[] { }, exception);
        }

        #endregion

        #region Internal log methods using ErrorCode categorization.

        internal void Verbose(ErrorCode errorCode, string format, params object[] args)
        {
            Log((int)errorCode, Severity.Verbose, format, args, null);
        }
        internal void Verbose2(ErrorCode errorCode, string format, params object[] args)
        {
            Log((int)errorCode, Severity.Verbose2, format, args, null);
        }
        internal void Verbose3(ErrorCode errorCode, string format, params object[] args)
        {
            Log((int)errorCode, Severity.Verbose3, format, args, null);
        }
        internal void Info(ErrorCode errorCode, string format, params object[] args)
        {
            Log((int)errorCode, Severity.Info, format, args, null);
        }
        internal void Warn(ErrorCode errorCode, string format, params object[] args)
        {
            Log((int)errorCode, Severity.Warning, format, args, null);
        }
        internal void Warn(ErrorCode errorCode, string message, Exception exception)
        {
            Log((int)errorCode, Severity.Warning, message, new object[] { }, exception);
        }
        internal void Error(ErrorCode errorCode, string message, Exception exception = null)
        {
            Log((int)errorCode, Severity.Error, message, new object[] { }, exception);
        }

        #endregion

        // an internal method to be used only by the runtime to ensure certain long report messages are logged fully, without truncating and bulking.
        internal void LogWithoutBulkingAndTruncating(Severity severityLevel, ErrorCode errorCode, string format, params object[] args)
        {
            if (severityLevel > SeverityLevel)
            {
                return;
            }

            string message = FormatMessageText(format, args);
            // skip bulking
            // break into chunks of smaller sizes 
            if (message.Length > MAX_LOG_MESSAGE_SIZE)
            {
                int startIndex = 0;
                int maxChunkSize = MAX_LOG_MESSAGE_SIZE - 100; // 100 bytes to allow slack and prefix.
                int partNum = 1;
                while (startIndex < message.Length)
                {
                    int chunkSize = (startIndex + maxChunkSize) < message.Length ? maxChunkSize : (message.Length - startIndex);
                    var messageToLog = String.Format("CHUNKED MESSAGE Part {0}: {1}", partNum, message.Substring(startIndex, chunkSize));
                    WriteLogMessage((int)errorCode, severity, messageToLog, null, null);
                    startIndex += chunkSize;
                    partNum++;
                }
            }
            else
            {
                WriteLogMessage((int)errorCode, severityLevel, message, null, null);
            }
        }

        private void Log(int errorCode, Severity sev, string format, object[] args, Exception exception)
        {
            if (sev > SeverityLevel)
            {
                return;
            }

            if (errorCode == 0 && loggerType == LoggerType.Runtime)
            {
                errorCode = (int)ErrorCode.Runtime;
            }

            if (CheckBulkMessageLimits(errorCode, sev))
            {
                WriteLogMessage(errorCode, sev, format, args, exception);
            }
        }

        internal bool CheckBulkMessageLimits(int logCode, Severity sev)
        {
            var now = DateTime.UtcNow;
            int count;
            TimeSpan sinceInterval;
            Dictionary<int, int> copyMessageCounts = null;

            bool isExcluded = excludedBulkLogCodes.Contains(logCode)
                              || (sev == Severity.Verbose || sev == Severity.Verbose2 || sev == Severity.Verbose3);

            lock (this)
            {
                sinceInterval = now - lastBulkLogMessageFlush;
                if (sinceInterval >= BulkMessageInterval)
                {
                    // Take local copy of buffered log message counts, now that this bulk message compaction period has finished
                    copyMessageCounts = recentLogMessageCounts;
                    recentLogMessageCounts = new Dictionary<int, int>();
                    lastBulkLogMessageFlush = now;
                }

                // Increment recent message counts, if appropriate
                if (isExcluded)
                {
                    count = 1;
                    // and don't track counts
                }
                else if (recentLogMessageCounts.ContainsKey(logCode))
                {
                    count = ++recentLogMessageCounts[logCode];
                }
                else
                {
                    recentLogMessageCounts.Add(logCode, 1);
                    count = 1;
                }
            }

            // Output any pending bulk compaction messages
            if (copyMessageCounts != null && copyMessageCounts.Count > 0)
            {
                object[] args = new object[4];
                args[3] = sinceInterval;

                // Output summary counts for any pending bulk message occurrances
                foreach (int ec in copyMessageCounts.Keys)
                {
                    int num = copyMessageCounts[ec] - BulkMessageLimit;

                    // Only output log codes which exceeded limit threshold
                    if (num > 0)
                    {
                        args[0] = ec;
                        args[1] = num;
                        args[2] = (num == 1) ? "" : "s";

                        WriteLogMessage(ec + BulkMessageSummaryOffset, Severity.Info, "Log code {0} occurred {1} additional time{2} in the previous {3}", args, null);
                    }
                }
            }

            // Should the current log message be output?
            return isExcluded || (count <= BulkMessageLimit);
        }

        private static string FormatMessageText(string format, object[] args)
        {
            // avoids exceptions if format string contains braces in calls that were not
            // designed to use format strings
            return (args == null || args.Length == 0) ? format : String.Format(format, args);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void WriteLogMessage(int errorCode, Severity sev, string format, object[] args, Exception exception)
        {
            string message = FormatMessageText(format, args);

            bool logMessageTruncated = false;
            if (message.Length > MAX_LOG_MESSAGE_SIZE)
            {
                message = String.Format("{0}. MESSAGE TRUNCATED AT THIS POINT!! Max message size = {1}", message.Substring(0, MAX_LOG_MESSAGE_SIZE), MAX_LOG_MESSAGE_SIZE);
                logMessageTruncated = true;
            }

            foreach (ILogConsumer consumer in LogConsumers)
            {
                try
                {
                    consumer.Log(sev, loggerType, logName, message, MyIPEndPoint, exception, errorCode);

                    if (logMessageTruncated)
                    {
                        consumer.Log(Severity.Warning, loggerType, logName,
                            "Previous log message was truncated - Max size = " + MAX_LOG_MESSAGE_SIZE,
                            MyIPEndPoint, exception,
                            (int)ErrorCode.Logger_LogMessageTruncated);
                    }
                }
                catch (Exception exc)
                {
                    Console.WriteLine("Exception while passing a log message to log consumer. TraceLogger type:{0}, name:{1}, severity:{2}, message:{3}, error code:{4}, message exception:{5}, log consumer exception:{6}",
                        consumer.GetType().FullName, logName, sev, message, errorCode, exception, exc);
                }
            }
        }

        /// <summary>
        /// Utility function to convert a <c>DateTime</c> object into printable data format used by the TraceLogger subsystem.
        /// </summary>
        /// <param name="exception">The <c>DateTime</c> value to be printed.</param>
        /// <returns>Formatted string representation of the input data, in the printable format used by the TraceLogger subsystem.</returns>
        public static string PrintDate(DateTime date)
        {
            return date.ToString(DATE_FORMAT, CultureInfo.InvariantCulture);
        }

        internal static DateTime ParseDate(string dateStr)
        {
            return DateTime.ParseExact(dateStr, DATE_FORMAT, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Utility function to convert a <c>DateTime</c> object into printable time format used by the TraceLogger subsystem.
        /// </summary>
        /// <param name="exception">The <c>DateTime</c> value to be printed.</param>
        /// <returns>Formatted string representation of the input data, in the printable format used by the TraceLogger subsystem.</returns>
        public static string PrintTime(DateTime date)
        {
            return date.ToString(TIME_FORMAT, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Utility function to convert an exception into printable format, including expanding and formatting any nested sub-expressions.
        /// </summary>
        /// <param name="exception">The exception to be printed.</param>
        /// <returns>Formatted string representation of the exception, including expanding and formatting any nested sub-expressions.</returns>
        public static string PrintException(Exception exception)
        {
            return exception == null ? String.Empty : PrintException_Helper(exception, 0, true);
        }

        public static string PrintExceptionWithoutStackTrace(Exception exception)
        {
            return exception == null ? String.Empty : PrintException_Helper(exception, 0, false);
        }

        private static string PrintException_Helper(Exception exception, int level, bool includeStackTrace)
        {
            if (exception == null) return String.Empty;
            var sb = new StringBuilder();
            sb.Append(PrintOneException(exception, level, includeStackTrace));
            if (exception is ReflectionTypeLoadException)
            {
                Exception[] loaderExceptions =
                    ((ReflectionTypeLoadException)exception).LoaderExceptions;
                if (loaderExceptions == null || loaderExceptions.Length == 0)
                {
                    sb.Append("No LoaderExceptions found");
                }
                else
                {
                    foreach (Exception inner in loaderExceptions)
                    {
                        // call recursively on all loader exceptions. Same level for all.
                        sb.Append(PrintException_Helper(inner, level + 1, includeStackTrace));
                    }
                }
            }
            else if (exception is AggregateException)
            {
                var innerExceptions = ((AggregateException)exception).InnerExceptions;
                if (innerExceptions == null) return sb.ToString();

                foreach (Exception inner in innerExceptions)
                {
                    // call recursively on all inner exceptions. Same level for all.
                    sb.Append(PrintException_Helper(inner, level + 1, includeStackTrace));
                }
            }
            else if (exception.InnerException != null)
            {
                // call recursively on a single inner exception.
                sb.Append(PrintException_Helper(exception.InnerException, level + 1, includeStackTrace));
            }
            return sb.ToString();
        }

        private static string PrintOneException(Exception exception, int level, bool includeStackTrace)
        {
            if (exception == null) return String.Empty;
            string stack = String.Empty;
            if (includeStackTrace && exception.StackTrace != null)
            {
                stack = String.Format(Environment.NewLine + exception.StackTrace);
            }
            string message = exception.Message;
            if (exception is StorageException)
            {
                message = PrintStorageException(exception as StorageException);
            }
            return String.Format(Environment.NewLine + "Exc level {0}: {1}: {2}{3}",
                level,
                exception.GetType(),
                message,
                stack);
        }

        private static string PrintStorageException(StorageException storeExc)
        {
            var result = storeExc.RequestInformation;
            if (result == null) return storeExc.Message;
            var extendedError = storeExc.RequestInformation.ExtendedErrorInformation;
            if (extendedError==null)
            {
                return String.Format("Message = {0}, HttpStatusCode = {1}, HttpStatusMessage = {2}.",
                        storeExc.Message,
                        result.HttpStatusCode,
                        result.HttpStatusMessage);

            }
            return String.Format("Message = {0}, HttpStatusCode = {1}, HttpStatusMessage = {2}, " +
                                   "ExtendedErrorInformation.ErrorCode = {3}, ExtendedErrorInformation.ErrorMessage = {4}{5}.",
                        storeExc.Message,
                        result.HttpStatusCode,
                        result.HttpStatusMessage,
                        extendedError.ErrorCode,
                        extendedError.ErrorMessage,
                        (extendedError.AdditionalDetails != null && extendedError.AdditionalDetails.Count > 0) ?
                            String.Format(", ExtendedErrorInformation.AdditionalDetails = {0}", Utils.DictionaryToString(extendedError.AdditionalDetails)) : String.Empty);
        }

        internal void Assert(ErrorCode errorCode, bool condition, string message = null)
        {
            if (condition) return;

            if (message == null)
            {
                message = "Internal contract assertion has failed!";
            }

            Fail(errorCode, "Assert failed with message = " + message);
        }

        internal void Fail(ErrorCode errorCode, string message)
        {
            if (message == null)
            {
                message = "Internal Fail!";
            }

            if (errorCode == 0)
            {
                errorCode = ErrorCode.Runtime;
            }

            Error(errorCode, "INTERNAL FAILURE! About to crash! Fail message is: " + message + Environment.NewLine + Environment.StackTrace);

            // Create mini-dump of this failure, for later diagnosis
            var dumpFile = CreateMiniDump();
            Error(ErrorCode.Logger_MiniDumpCreated, "INTERNAL FAILURE! Application mini-dump written to file " + dumpFile.FullName);

            Flush(); // Flush logs to disk

            // Kill process
            if (Debugger.IsAttached)
            {
                Debugger.Break();
            }
            else
            {
                Error(ErrorCode.Logger_ProcessCrashing, "INTERNAL FAILURE! Process crashing!");
                Close();

                Environment.FailFast("Unrecoverable failure: " + message);
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

            var thisAssembly = (Assembly.GetEntryAssembly() ?? Assembly.GetCallingAssembly()) ?? Assembly.GetExecutingAssembly();

            var dumpFileName = string.Format(@"{0}-MiniDump-{1}.dmp",
                thisAssembly.GetName().Name,
                DateTime.UtcNow.ToString(dateFormat, CultureInfo.InvariantCulture));

            using (var stream = File.Create(dumpFileName))
            {
                var process = Process.GetCurrentProcess();

                // It is safe to call DangerousGetHandle() here because the process is already crashing.
                NativeMethods.MiniDumpWriteDump(
                    process.Handle,
                    process.Id,
                    stream.SafeFileHandle.DangerousGetHandle(),
                    dumpType,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }

            return new FileInfo(dumpFileName);
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
