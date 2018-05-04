using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace Orleans.Runtime
{
    /// <summary>
    /// Extension methods which preserves legacy orleans log methods style
    /// </summary>
    public static class OrleansLoggerExtension
    {
        /// <summary>
        /// Writes a log entry at the Debug severity level.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="format">Format string of the log message with named parameters
        /// <remarks>Not always suitable for <c>String.Format</c>. See Microsoft.Extensions.Logging MessageTemplate section for more information. Suggest to use their pattern over this extension method</remarks>
        /// </param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Debug(this ILogger logger, string format, params object[] args)
        {
            logger.LogDebug(format, args);
        }

        /// <summary>
        /// Writes a log entry at the Verbose severity level.
        /// Verbose is suitable for debugging information that should usually not be logged in production.
        /// Verbose is lower than Info.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="message">The log message.</param>
        public static void Debug(this ILogger logger, string message)
        {
            logger.LogDebug(message);
        }

        /// <summary>
        /// Writes a log entry at the Trace logLevel.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="format">Format string of the log message with named parameters
        /// <remarks>Not always suitable for <c>String.Format</c>. See Microsoft.Extensions.Logging MessageTemplate section for more information. Suggest to use their pattern over this extension method</remarks>
        /// </param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Trace(this ILogger logger, string format, params object[] args)
        {
            logger.LogTrace(format, args);
        }

        /// <summary>
        /// Writes a log entry at the Verbose2 severity level.
        /// Verbose2 is lower than Verbose.
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="message">The log message.</param>
        public static void Trace(this ILogger logger, string message)
        {
            logger.LogTrace(message);
        }

        /// <summary>
        /// Writes a log entry at the Information Level
        /// </summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="format">Format string of the log message with named parameters
        /// <remarks>Not always suitable for <c>String.Format</c>. See Microsoft.Extensions.Logging MessageTemplate section for more information. Suggest to use their pattern over this extension method</remarks>
        /// </param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Info(this ILogger logger, string format, params object[] args)
        {
            logger.LogInformation(format, args);
        }

        /// <summary>
        /// Writes a log entry at the Info logLevel 
        /// </summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="message">The log message.</param>
        public static void Info(this ILogger logger, string message)
        {
            logger.LogInformation(message);
        }

        /// <summary>
        /// Writes a log entry at the Debug logLevel
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">Format string of the log message with named parameters
        /// <remarks>Not always suitable for <c>String.Format</c>. See Microsoft.Extensions.Logging MessageTemplate section for more information. Suggest to use their pattern over this extension method</remarks>
        /// </param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Debug(this ILogger logger, int logCode, string format, params object[] args)
        {
            logger.LogDebug(logCode, format, args);
        }

        internal static void Debug(this ILogger logger, ErrorCode logCode, string format, params object[] args)
        {
            logger.LogDebug(LoggingUtils.CreateEventId(logCode), format, args);
        }

        /// <summary>
        /// Writes a log entry at the Debug logLevel
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The log message.</param>
        public static void Debug(this ILogger logger, int logCode, string message)
        {
            logger.LogDebug(logCode, message);
        }

        internal static void Debug(this ILogger logger, ErrorCode logCode, string message)
        {
            logger.LogDebug(LoggingUtils.CreateEventId(logCode), message);
        }

        /// <summary>
        /// Writes a log entry at the Trace logLevel
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">Format string of the log message with named parameters
        /// <remarks>Not always suitable for <c>String.Format</c>. See Microsoft.Extensions.Logging MessageTemplate section for more information. Suggest to use their pattern over this extension method</remarks>
        /// </param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Trace(this ILogger logger, int logCode, string format, params object[] args)
        {
            logger.LogTrace(logCode, format, args);
        }

        internal static void Trace(this ILogger logger, ErrorCode logCode, string format, params object[] args)
        {
            logger.LogTrace(LoggingUtils.CreateEventId(logCode), format, args);
        }

        /// <summary>
        /// Writes a log entry at the Trace logLevel
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The log message.</param>
        public static void Trace(this ILogger logger, int logCode, string message)
        {
            logger.LogTrace(logCode, message);
        }

        internal static void Trace(this ILogger logger, ErrorCode logCode, string message)
        {
            logger.LogTrace(LoggingUtils.CreateEventId(logCode), message);
        }

        /// <summary>
        /// Writes a log entry at the Information logLevel
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">Format string of the log message with named parameters
        /// <remarks>Not always suitable for <c>String.Format</c>. See Microsoft.Extensions.Logging MessageTemplate section for more information. Suggest to use their pattern over this extension method</remarks>
        /// </param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Info(this ILogger logger, int logCode, string format, params object[] args)
        {
            logger.LogInformation(logCode, format, args);
        }

        internal static void Info(this ILogger logger, ErrorCode logCode, string format, params object[] args)
        {
            logger.LogInformation(LoggingUtils.CreateEventId(logCode), format, args);
        }

        /// <summary>
        /// Writes a log entry at the Information logLevel
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The log message.</param>
        public static void Info(this ILogger logger, int logCode, string message)
        {
            logger.LogInformation(logCode, message);
        }

        internal static void Info(this ILogger logger, ErrorCode logCode, string message)
        {
            logger.LogInformation(LoggingUtils.CreateEventId(logCode), message);
        }

        /// <summary>
        /// Writes a log entry at the Warning level
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">Format string of the log message with named parameters
        /// <remarks>Not always suitable for <c>String.Format</c>. See Microsoft.Extensions.Logging MessageTemplate section for more information. Suggest to use their pattern over this extension method</remarks>
        /// </param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Warn(this ILogger logger, int logCode, string format, params object[] args)
        {
            logger.LogWarning(logCode, format, args);
        }

        internal static void Warn(this ILogger logger, ErrorCode logCode, string format, params object[] args)
        {
            logger.LogWarning(LoggingUtils.CreateEventId(logCode), format, args);
        }

        /// <summary>
        /// Writes a log entry at the Warning level
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The warning message to log.</param>
        /// <param name="exception">An exception related to the warning, if any.</param>
        public static void Warn(this ILogger logger, int logCode, string message, Exception exception = null)
        {
            logger.LogWarning(logCode, exception, message);
        }

        internal static void Warn(this ILogger logger, ErrorCode logCode, string message, Exception exception = null)
        {
            logger.LogWarning(LoggingUtils.CreateEventId(logCode), exception, message);
        }

        /// <summary>
        /// Writes a log entry at the Error level
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The error message to log.</param>
        /// <param name="exception">An exception related to the error, if any.</param>
        public static void Error(this ILogger logger, int logCode, string message, Exception exception = null)
        {
            logger.LogError(logCode, exception, message);
        }

        internal static void Error(this ILogger logger, ErrorCode logCode, string message, Exception exception = null)
        {
            logger.LogError(LoggingUtils.CreateEventId(logCode), exception, message);
        }


        internal static void Assert(this ILogger logger, ErrorCode errorCode, bool condition, string message = null)
        {
            if (condition) return;

            if (message == null)
            {
                message = "Internal contract assertion has failed!";
            }

            logger.Fail(errorCode, "Assert failed with message = " + message);
        }

        internal static void Fail(this ILogger logger, ErrorCode errorCode, string message)
        {
            if (message == null)
            {
                message = "Internal Fail!";
            }

            if (errorCode == 0)
            {
                errorCode = ErrorCode.Runtime;
            }

            logger.Error(errorCode, "INTERNAL FAILURE! About to crash! Fail message is: " + message + Environment.NewLine + Environment.StackTrace);

            // Kill process
            if (Debugger.IsAttached)
            {
                Debugger.Break();
            }
            else
            {
                logger.Error(ErrorCode.Logger_ProcessCrashing, "INTERNAL FAILURE! Process crashing!");

                Environment.FailFast("Unrecoverable failure: " + message);
            }
        }
    }
}