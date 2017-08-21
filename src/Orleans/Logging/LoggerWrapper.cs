using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace Orleans.Runtime
{
    //TODO: Mark it as [Obsolete] after all runtime has migrated
    internal class LoggerWrapper : Logger
    {
        public override Severity SeverityLevel => this.maxSeverityLevel;
        public override string Name => this.name;
        private string name;
        private readonly Severity maxSeverityLevel;
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
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

        //TODO: delete those APM methods after Julian's PR. 
        public override void DecrementMetric(string name)
        {
            throw new NotImplementedException();
        }

        public override void DecrementMetric(string name, double value)
        {
            throw new NotImplementedException();
        }

        public override void IncrementMetric(string name)
        {
            throw new NotImplementedException();
        }

        public override void IncrementMetric(string name, double value)
        {
            throw new NotImplementedException();
        }
        public override void TrackDependency(string name, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success)
        {
            throw new NotImplementedException();
        }

        public override void TrackEvent(string name, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            throw new NotImplementedException();
        }

        public override void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            throw new NotImplementedException();
        }

        public override void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            throw new NotImplementedException();
        }

        public override void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
            throw new NotImplementedException();
        }

        public override void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success)
        {
            throw new NotImplementedException();
        }

        public override void TrackTrace(string message)
        {
            throw new NotImplementedException();
        }

        public override void TrackTrace(string message, Severity severityLevel)
        {
            throw new NotImplementedException();
        }

        public override void TrackTrace(string message, Severity severityLevel, IDictionary<string, string> properties)
        {
            throw new NotImplementedException();
        }

        public override void TrackTrace(string message, IDictionary<string, string> properties)
        {
            throw new NotImplementedException();
        }
    }
}
