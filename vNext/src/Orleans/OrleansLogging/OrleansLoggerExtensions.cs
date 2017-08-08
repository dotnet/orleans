using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Extensions.Logging
{
    public static class OrleansLoggerExtension
    {
        /// <summary>
        /// Extension method to preserve legacy orleans logging method <see cref="Logger.Log(this ILogger logger, int errorCode, Severity sev, string format, object[] args, Exception exception)">,
        /// and legacy message bulking feature. Message bulking feature is only supported when OrleansLoggerProvider is added to ILoggerFactory. 
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="errorCode"></param>
        /// <param name="sev"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        /// <param name="exception"></param>
        public static void Log(this ILogger logger, int errorCode, Severity sev, string format, object[] args, Exception exception)
        {
            logger.Log<string>(OrleansLogger.SeverityToLogLevel(sev), OrleansLogger.CreateEventId(0, errorCode), FormatMessageText(format, args), exception,
                (message, exc) => exc == null ? message : $"{message}, Exception: {exc}");
        }

        private static readonly object[] EmptyObjectArray = new object[0];

        /// <summary>
        /// Writes a log entry at the Verbose severity level.
        /// Verbose is suitable for debugging information that should usually not be logged in production.
        /// Verbose is lower than Info.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Verbose(this ILogger logger, string format, params object[] args)
        {
            logger.Log(0, Severity.Verbose, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose severity level.
        /// Verbose is suitable for debugging information that should usually not be logged in production.
        /// Verbose is lower than Info.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="message">The log message.</param>
        public static void Verbose(this ILogger logger, string message)
        {
            logger.Log(0, Severity.Verbose, message, EmptyObjectArray, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose2 severity level.
        /// Verbose2 is lower than Verbose.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Verbose2(this ILogger logger, string format, params object[] args)
        {
            logger.Log(0, Severity.Verbose2, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose2 severity level.
        /// Verbose2 is lower than Verbose.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="message">The log message.</param>
        public static void Verbose2(this ILogger logger, string message)
        {
            logger.Log(0, Severity.Verbose2, message, EmptyObjectArray, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose3 severity level.
        /// Verbose3 is the lowest severity level.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Verbose3(this ILogger logger, string format, params object[] args)
        {
            logger.Log(0, Severity.Verbose3, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose3 severity level.
        /// Verbose3 is the lowest severity level.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="message">The log message.</param>
        public static void Verbose3(this ILogger logger, string message)
        {
            logger.Log(0, Severity.Verbose3, message, EmptyObjectArray, null);
        }

        /// <summary>
        /// Writes a log entry at the Info severity level.
        /// Info is suitable for information that does not indicate an error but that should usually be logged in production.
        /// Info is lower than Warning.
        /// </summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Info(this ILogger logger, string format, params object[] args)
        {
            logger.Log(0, Severity.Info, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Info severity level.
        /// Info is suitable for information that does not indicate an error but that should usually be logged in production.
        /// Info is lower than Warning.
        /// </summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="message">The log message.</param>
        public static void Info(this ILogger logger, string message)
        {
            logger.Log(0, Severity.Info, message, EmptyObjectArray, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose severity level, with the specified log id code.
        /// Verbose is suitable for debugging information that should usually not be logged in production.
        /// Verbose is lower than Info.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Verbose(this ILogger logger, int logCode, string format, params object[] args)
        {
            logger.Log(logCode, Severity.Verbose, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose severity level, with the specified log id code.
        /// Verbose is suitable for debugging information that should usually not be logged in production.
        /// Verbose is lower than Info.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The log message.</param>
        public static void Verbose(this ILogger logger, int logCode, string message)
        {
            logger.Log(logCode, Severity.Verbose, message, EmptyObjectArray, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose2 severity level, with the specified log id code.
        /// Verbose2 is lower than Verbose.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Verbose2(this ILogger logger, int logCode, string format, params object[] args)
        {
            logger.Log(logCode, Severity.Verbose2, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose2 severity level, with the specified log id code.
        /// Verbose2 is lower than Verbose.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The log message.</param>
        public static void Verbose2(this ILogger logger, int logCode, string message)
        {
            logger.Log(logCode, Severity.Verbose2, message, EmptyObjectArray, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose3 severity level, with the specified log id code.
        /// Verbose3 is the lowest severity level.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Verbose3(this ILogger logger, int logCode, string format, params object[] args)
        {
            logger.Log(logCode, Severity.Verbose3, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose3 severity level, with the specified log id code.
        /// Verbose3 is the lowest severity level.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The log message.</param>
        public static void Verbose3(this ILogger logger, int logCode, string message)
        {
            logger.Log(logCode, Severity.Verbose3, message, EmptyObjectArray, null);
        }

        /// <summary>
        /// Writes a log entry at the Info severity level, with the specified log id code.
        /// Info is suitable for information that does not indicate an error but that should usually be logged in production.
        /// Info is lower than Warning.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Info(this ILogger logger, int logCode, string format, params object[] args)
        {
            logger.Log(logCode, Severity.Info, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Info severity level, with the specified log id code.
        /// Info is suitable for information that does not indicate an error but that should usually be logged in production.
        /// Info is lower than Warning.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The log message.</param>
        public static void Info(this ILogger logger, int logCode, string message)
        {
            logger.Log(logCode, Severity.Info, message, EmptyObjectArray, null);
        }

        /// <summary>
        /// Writes a log entry at the Warning severity level, with the specified log id code.
        /// Warning is suitable for problem conditions that the system or application can handle by itself,
        /// but that the administrator should be aware of.
        /// Typically these are situations that are expected but that may eventually require an administrative
        /// response if they recur.
        /// Warning is lower than Error.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Warn(this ILogger logger, int logCode, string format, params object[] args)
        {
            logger.Log(logCode, Severity.Warning, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Warning severity level, with the specified log id code.
        /// Warning is suitable for problem conditions that the system or application can handle by itself,
        /// but that the administrator should be aware of.
        /// Typically these are situations that are expected but that may eventually require an administrative
        /// response if they recur.
        /// Warning is lower than Error.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The warning message to log.</param>
        /// <param name="exception">An exception related to the warning, if any.</param>
        public static void Warn(this ILogger logger, int logCode, string message, Exception exception = null)
        {
            logger.Log(logCode, Severity.Warning, message, EmptyObjectArray, exception);
        }

        /// <summary>
        /// Writes a log entry at the Error severity level, with the specified log id code.
        /// Error is suitable for problem conditions that require immediate administrative response.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The error message to log.</param>
        /// <param name="exception">An exception related to the error, if any.</param>
        public static void Error(this ILogger logger, int logCode, string message, Exception exception = null)
        {
            logger.Log(logCode, Severity.Error, message, EmptyObjectArray, exception);
        }

        private static string FormatMessageText(string format, object[] args)
        {
            // avoids exceptions if format string contains braces in calls that were not
            // designed to use format strings
            return (args == null || args.Length == 0) ? format : String.Format(format, args);
        }
    }
}
