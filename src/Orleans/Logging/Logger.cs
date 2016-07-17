using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Orleans.Runtime
{
    /// <summary>
    /// Interface of Orleans runtime for logging services. 
    /// </summary>
    [Serializable]
    public abstract class Logger
    {
        /// <summary> Current SeverityLevel set for this logger. </summary>
        public abstract Severity SeverityLevel { get; }

        /// <summary>
        /// Name of logger instance
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Find existing or create new Logger with the specified name
        /// </summary>
        /// <param name="loggerName">Name of the Logger to find or create</param>
        /// <returns>Logger associated with the specified name</returns>
        public abstract Logger GetLogger(string loggerName);

        /// <summary>
        /// Log message
        /// </summary>
        /// <param name="errorCode"></param>
        /// <param name="sev"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        /// <param name="exception"></param>
        public abstract void Log(int errorCode, Severity sev, string format, object[] args, Exception exception);

        /// <summary> Whether the current SeverityLevel would output <c>Warning</c> messages for this logger. </summary>
        [DebuggerHidden]
        public bool IsWarning => SeverityLevel >= Severity.Warning;

        /// <summary> Whether the current SeverityLevel would output <c>Info</c> messages for this logger. </summary>
        [DebuggerHidden]
        public bool IsInfo => SeverityLevel >= Severity.Info;

        /// <summary> Whether the current SeverityLevel would output <c>Verbose</c> messages for this logger. </summary>
        [DebuggerHidden]
        public bool IsVerbose => SeverityLevel >= Severity.Verbose;

        /// <summary> Whether the current SeverityLevel would output <c>Verbose2</c> messages for this logger. </summary>
        [DebuggerHidden]
        public bool IsVerbose2 => SeverityLevel >= Severity.Verbose2;

        /// <summary> Whether the current SeverityLevel would output <c>Verbose3</c> messages for this logger. </summary>
        [DebuggerHidden]
        public bool IsVerbose3 => SeverityLevel >= Severity.Verbose3;

        #region APM Methods

        public abstract void TrackDependency(string name, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success);
        //public abstract void TrackDependency(DependencyTelemetry telemetry);
        //public abstract void TrackEvent(EventTelemetry telemetry);
        public abstract void TrackEvent(string name, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null);
        //public abstract void TrackMetric(MetricTelemetry telemetry);
        public abstract void TrackMetric(string name, double value, IDictionary<string, string> properties = null);
        public abstract void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null);
        public abstract void IncrementMetric(string name);
        public abstract void IncrementMetric(string name, double value);
        public abstract void DecrementMetric(string name);
        public abstract void DecrementMetric(string name, double value);
        //public abstract void TrackRequest(RequestTelemetry request);
        public abstract void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success);
        
        //public abstract void TrackException(ExceptionTelemetry telemetry);
        public abstract void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null);
        public abstract void TrackTrace(string message);
        public abstract void TrackTrace(string message, Severity severityLevel);
        public abstract void TrackTrace(string message, Severity severityLevel, IDictionary<string, string> properties);
        public abstract void TrackTrace(string message, IDictionary<string, string> properties);
        //public abstract void TrackTrace(TraceTelemetry telemetry);

        #endregion
    }
}
