
using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.TestingHost.Utils
{
    /// <summary>
    /// Test logger that does nothing with the logs.
    /// </summary>
    public class NoOpTestLogger : Logger
    {
        /// <summary>
        /// Singleton instance of logger
        /// </summary>
        public static Logger Instance = new NoOpTestLogger();

        /// <summary> Logger is off. </summary>
        public override Severity SeverityLevel => Severity.Off;

        /// <summary>
        /// Name of logger instance
        /// </summary>
        public override string Name => "NoOpTestLogger";

        /// <summary>
        /// Find existing or create new Logger with the specified name
        /// </summary>
        /// <param name="loggerName">Name of the Logger to find or create</param>
        /// <returns>Logger associated with the specified name</returns>
        public override Logger GetLogger(string loggerName)
        {
            return Instance;
        }

        /// <summary>
        /// Log message does nothing
        /// </summary>
        /// <param name="errorCode"></param>
        /// <param name="sev"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        /// <param name="exception"></param>
        public override void Log(int errorCode, Severity sev, string format, object[] args, Exception exception)
        {
        }

        /// <summary>
        /// Track dependency does nothing
        /// </summary>
        /// <param name="name"></param>
        /// <param name="commandName"></param>
        /// <param name="startTime"></param>
        /// <param name="duration"></param>
        /// <param name="success"></param>
        /// <exception cref="NotImplementedException"></exception>
        public override void TrackDependency(string name, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success)
        {
        }

        /// <summary>
        /// Track event does nothing
        /// </summary>
        /// <param name="name"></param>
        /// <param name="properties"></param>
        /// <param name="metrics"></param>
        public override void TrackEvent(string name, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
        }

        /// <summary>
        /// Track metric does nothing
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        /// <param name="properties"></param>
        public override void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
        }

        /// <summary>
        /// Track metric does nothing
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        /// <param name="properties"></param>
        public override void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
        }

        /// <summary>
        /// Increment metric does nothing
        /// </summary>
        /// <param name="name"></param>
        public override void IncrementMetric(string name)
        {
        }

        /// <summary>
        /// Increment metric does nothing
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public override void IncrementMetric(string name, double value)
        {
        }

        /// <summary>
        /// Decrement metric does nothing
        /// </summary>
        /// <param name="name"></param>
        public override void DecrementMetric(string name)
        {
        }

        /// <summary>
        /// Decrement metric does nothing
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public override void DecrementMetric(string name, double value)
        {
        }

        /// <summary>
        /// Track request does nothing
        /// </summary>
        /// <param name="name"></param>
        /// <param name="startTime"></param>
        /// <param name="duration"></param>
        /// <param name="responseCode"></param>
        /// <param name="success"></param>
        public override void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success)
        {
        }

        /// <summary>
        /// Track exception does nothing
        /// </summary>
        /// <param name="exception"></param>
        /// <param name="properties"></param>
        /// <param name="metrics"></param>
        public override void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
        }

        /// <summary>
        /// Track trace does nothing
        /// </summary>
        /// <param name="message"></param>
        public override void TrackTrace(string message)
        {
        }

        /// <summary>
        /// Track trace does nothing
        /// </summary>
        /// <param name="message"></param>
        /// <param name="severityLevel"></param>
        public override void TrackTrace(string message, Severity severityLevel)
        {
        }

        /// <summary>
        /// Track trace does nothing
        /// </summary>
        /// <param name="message"></param>
        /// <param name="severityLevel"></param>
        /// <param name="properties"></param>
        public override void TrackTrace(string message, Severity severityLevel, IDictionary<string, string> properties)
        {
        }

        /// <summary>
        /// Track trace does nothing
        /// </summary>
        /// <param name="message"></param>
        /// <param name="properties"></param>
        public override void TrackTrace(string message, IDictionary<string, string> properties)
        {
        }
    }
}
