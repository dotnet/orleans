﻿
using System;
using System.Diagnostics;

namespace Orleans.Runtime
{
    public static class LoggerExtensions
    {
        /// <summary>
        /// Finds or creates a logger named after the existing logger with the appended name added.
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="loggerName"></param>
        /// <returns></returns>
        public static Logger GetSubLogger(this Logger logger, string appendedName)
        {
            return logger.GetLogger(logger.Name + appendedName);
        }

        /// <summary>
        /// Writes a log entry at the Verbose severity level.
        /// Verbose is suitable for debugging information that should usually not be logged in production.
        /// Verbose is lower than Info.
        /// </summary>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Verbose(this Logger logger, string format, params object[] args)
        {
            logger.Log(0, Severity.Verbose, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose2 severity level.
        /// Verbose2 is lower than Verbose.
        /// </summary>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Verbose2(this Logger logger, string format, params object[] args)
        {
            logger.Log(0, Severity.Verbose2, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose3 severity level.
        /// Verbose3 is the lowest severity level.
        /// </summary>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Verbose3(this Logger logger, string format, params object[] args)
        {
            logger.Log(0, Severity.Verbose3, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Info severity level.
        /// Info is suitable for information that does not indicate an error but that should usually be logged in production.
        /// Info is lower than Warning.
        /// </summary>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Info(this Logger logger, string format, params object[] args)
        {
            logger.Log(0, Severity.Info, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose severity level, with the specified log id code.
        /// Verbose is suitable for debugging information that should usually not be logged in production.
        /// Verbose is lower than Info.
        /// </summary>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Verbose(this Logger logger, int logCode, string format, params object[] args)
        {
            logger.Log(logCode, Severity.Verbose, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose2 severity level, with the specified log id code.
        /// Verbose2 is lower than Verbose.
        /// </summary>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Verbose2(this Logger logger, int logCode, string format, params object[] args)
        {
            logger.Log(logCode, Severity.Verbose2, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Verbose3 severity level, with the specified log id code.
        /// Verbose3 is the lowest severity level.
        /// </summary>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Verbose3(this Logger logger, int logCode, string format, params object[] args)
        {
            logger.Log(logCode, Severity.Verbose3, format, args, null);
        }

        /// <summary>
        /// Writes a log entry at the Info severity level, with the specified log id code.
        /// Info is suitable for information that does not indicate an error but that should usually be logged in production.
        /// Info is lower than Warning.
        /// </summary>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="format">A standard format string, suitable for String.Format.</param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Info(this Logger logger, int logCode, string format, params object[] args)
        {
            logger.Log(logCode, Severity.Info, format, args, null);
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
        public static void Warn(this Logger logger, int logCode, string format, params object[] args)
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
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The warning message to log.</param>
        /// <param name="exception">An exception related to the warning, if any.</param>
        public static void Warn(this Logger logger, int logCode, string message, Exception exception = null)
        {
            logger.Log(logCode, Severity.Warning, message, new object[] { }, exception);
        }

        /// <summary>
        /// Writes a log entry at the Error severity level, with the specified log id code.
        /// Error is suitable for problem conditions that require immediate administrative response.
        /// </summary>
        /// <param name="logCode">The log code associated with this message.</param>
        /// <param name="message">The error message to log.</param>
        /// <param name="exception">An exception related to the error, if any.</param>
        public static void Error(this Logger logger, int logCode, string message, Exception exception = null)
        {
            logger.Log(logCode, Severity.Error, message, new object[] { }, exception);
        }

        #region Internal log methods using ErrorCode categorization.

        internal static void Verbose(this Logger logger, ErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Verbose, format, args, null);
        }
        internal static void Verbose2(this Logger logger, ErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Verbose2, format, args, null);
        }
        internal static void Verbose3(this Logger logger, ErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Verbose3, format, args, null);
        }
        internal static void Info(this Logger logger, ErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Info, format, args, null);
        }
        internal static void Warn(this Logger logger, ErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Warning, format, args, null);
        }
        internal static void Warn(this Logger logger, ErrorCode errorCode, string message, Exception exception)
        {
            logger.Log((int)errorCode, Severity.Warning, message, new object[] { }, exception);
        }
        internal static void Error(this Logger logger, ErrorCode errorCode, string message, Exception exception = null)
        {
            logger.Log((int)errorCode, Severity.Error, message, new object[] { }, exception);
        }

        internal static void Assert(this Logger logger, ErrorCode errorCode, bool condition, string message = null)
        {
            if (condition) return;

            if (message == null)
            {
                message = "Internal contract assertion has failed!";
            }

            logger.Fail(errorCode, "Assert failed with message = " + message);
        }

        internal static void Fail(this Logger logger, ErrorCode errorCode, string message)
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

            // Create mini-dump of this failure, for later diagnosis
            var dumpFile = LogManager.CreateMiniDump();
            logger.Error(ErrorCode.Logger_MiniDumpCreated, "INTERNAL FAILURE! Application mini-dump written to file " + dumpFile.FullName);

            LogManager.Flush(); // Flush logs to disk

            // Kill process
            if (Debugger.IsAttached)
            {
                Debugger.Break();
            }
            else
            {
                logger.Error(ErrorCode.Logger_ProcessCrashing, "INTERNAL FAILURE! Process crashing!");
                LogManager.Close();

                Environment.FailFast("Unrecoverable failure: " + message);
            }
        }

        #endregion
    }
}
