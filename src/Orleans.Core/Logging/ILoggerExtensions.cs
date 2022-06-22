using Microsoft.Extensions.Logging;
using System;

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

        public static void Debug(this ILogger logger, ErrorCode logCode, string format, params object[] args)
        {
            logger.LogDebug(new EventId((int)logCode), format, args);
        }

        public static void Debug(this ILogger logger, ErrorCode logCode, string message)
        {
            logger.LogDebug(new EventId((int)logCode), message);
        }

        public static void Trace(this ILogger logger, ErrorCode logCode, string format, params object[] args)
        {
            logger.LogTrace(new EventId((int)logCode), format, args);
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
    }
}