using Orleans.Logging.Legacy;
using System;
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
    }
}
