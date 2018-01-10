using Microsoft.Extensions.Logging;
using Orleans.Logging.Legacy;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace Orleans.Runtime
{
    [Obsolete(OrleansLoggingUtils.ObsoleteMessageStringForLegacyLoggingInfrastructure)]
    internal class LoggerWrapper<T> : Logger
    {
        private readonly LoggerWrapper logger;
        public LoggerWrapper(ILoggerFactory loggerFactory)
        {
            logger = new LoggerWrapper(typeof(T).FullName, loggerFactory);
        }

        public LoggerWrapper(ILogger logger, ILoggerFactory loggerFactory)
        {
            this.logger = new LoggerWrapper(logger, typeof(T).FullName, loggerFactory);
        }

        public override Severity SeverityLevel => this.logger.SeverityLevel;

        public override string Name => this.logger.Name;

        public override Logger GetLogger(string loggerName)
        {
            return this.logger.GetLogger(loggerName);
        }

        public override void Log(int errorCode, Severity sev, string format, object[] args, Exception exception)
        {
            this.logger.Log(errorCode, sev, format, args, exception);
        }
    }
    [Obsolete(OrleansLoggingUtils.ObsoleteMessageStringForLegacyLoggingInfrastructure)]
    internal class LoggerWrapper : Logger
    {
        public override Severity SeverityLevel => this.maxSeverityLevel;
        public override string Name => this.name;
        private string name;
        private readonly Severity maxSeverityLevel;
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;

        public LoggerWrapper(ILogger logger, string name, ILoggerFactory loggerFactory)
        {
            this.logger = logger;
            this.name = name;
            this.loggerFactory = loggerFactory;
            this.maxSeverityLevel = FindSeverityForLogger(this.logger);
        }

        public LoggerWrapper(string name, ILoggerFactory loggerFactory)
        {
            this.name = Name;
            this.logger = loggerFactory.CreateLogger(name);
            this.maxSeverityLevel = FindSeverityForLogger(logger);
            this.loggerFactory = loggerFactory;
        }

        public override void Log(int errorCode, Severity sev, string format, object[] args, Exception exception)
        {
            switch (SeverityToLogLevel(sev))
            {
                case LogLevel.Critical: logger.LogCritical(errorCode, exception, format, args);
                    break;
                case LogLevel.Error: logger.LogError(errorCode, exception, format, args);
                    break;
                case LogLevel.Warning: logger.LogWarning(errorCode, exception, format, args);
                    break;
                case LogLevel.Information: logger.LogInformation(errorCode, exception, format, args);
                    break;
                case LogLevel.Debug:
                    logger.LogDebug(errorCode, exception, format, args);
                    break;
                case LogLevel.Trace:
                    logger.LogTrace(errorCode, exception, format, args);
                    break;
            }
        }
        
        private static LogLevel SeverityToLogLevel(Severity severity)
        {
            switch (severity)
            {
                case Severity.Off: return LogLevel.None;
                case Severity.Error: return LogLevel.Error;
                case Severity.Warning: return LogLevel.Warning;
                case Severity.Info: return LogLevel.Information;
                case Severity.Verbose: return LogLevel.Debug;
                default: return LogLevel.Trace;
            }
        }

        private Severity FindSeverityForLogger(ILogger logger)
        {
            //traversal from the lowest LogLevel to the highest to find the Severity of current Logger
            //If Trace is enabled, then minimun enabled LogLevel is Trace, which maps to Severity being Verbose2
            if (logger.IsEnabled(LogLevel.Trace))
                return Severity.Verbose3;
            //If Trace is not enabled but Debug is enabled, then minimun enabled LogLevel is Debug, which maps to Severity being Verbose.
            if (logger.IsEnabled(LogLevel.Debug))
                return Severity.Verbose;
            //same logic as aboce
            if (logger.IsEnabled(LogLevel.Information))
                return Severity.Info;
            if (logger.IsEnabled(LogLevel.Warning))
                return Severity.Warning;
            if (logger.IsEnabled(LogLevel.Error) || logger.IsEnabled(LogLevel.Critical))
                return Severity.Error;
            if (logger.IsEnabled(LogLevel.None))
                return Severity.Off;
            //default;
            return Severity.Verbose3;
        }

        public override Logger GetLogger(string loggerName)
        {
            return new LoggerWrapper(loggerName, this.loggerFactory);
        }
    }
}
