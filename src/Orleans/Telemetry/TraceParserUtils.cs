using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;

namespace Orleans.Runtime
{
    internal static class TraceParserUtils
    {
        public static string PrintProperties(string message, IDictionary<string, string> properties)
        {
            if (properties == null || properties.Keys.Count == 0)
                return message;

            var sb = new StringBuilder(message + " - Properties:");
            sb.Append(" ");
            sb.Append("{");

            foreach (var key in properties.Keys)
            {
                sb.Append(" ");
                sb.Append(key);
                sb.Append(" : ");
                sb.Append(properties[key]);
                sb.Append(",");
            }
            sb.Remove(sb.Length - 1, 1);
            sb.Append(" ");
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// The method to call during logging.
        /// This method should be very fast, since it is called synchronously during Orleans logging.
        /// </summary>
        /// <remarks>
        /// To customize functionality in a log writter derived from this base class, 
        /// you should override the <c>FormatLogMessage</c> and/or <c>WriteLogMessage</c> 
        /// methods rather than overriding this method directly.
        /// </remarks>
        /// <param name="severity">The severity of the message being traced.</param>
        /// <param name="loggerType">The type of logger the message is being traced through.</param>
        /// <param name="caller">The name of the logger tracing the message.</param>
        /// <param name="myIPEndPoint">The <see cref="IPEndPoint"/> of the Orleans client/server if known. May be null.</param>
        /// <param name="message">The message to log.</param>
        /// <param name="exception">The exception to log. May be null.</param>
        /// <param name="errorCode">Numeric event code for this log entry. May be zero, meaning 'Unspecified'.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public static string FormatLogMessage(
            Severity severity,
            LoggerType loggerType,
            string caller,
            string message,
            IPEndPoint myIPEndPoint,
            Exception exception,
            int errorCode)
        {
            var now = DateTime.UtcNow;

            return FormatLogMessage(
                now,
                severity,
                loggerType,
                caller,
                message,
                myIPEndPoint,
                exception,
                errorCode);
        }

        /// <summary>The method to call during logging to format the log info into a string ready for output.</summary>
        /// <param name="timestamp">Timestamp of the log message.</param>
        /// <param name="severity">The severity of the message being traced.</param>
        /// <param name="loggerType">The type of logger the message is being traced through.</param>
        /// <param name="caller">The name of the logger tracing the message.</param>
        /// <param name="myIPEndPoint">The <see cref="IPEndPoint"/> of the Orleans client/server if known. May be null.</param>
        /// <param name="message">The message to log.</param>
        /// <param name="exception">The exception to log. May be null.</param>
        /// <param name="errorCode">Numeric event code for this log entry. May be zero, meaning 'Unspecified'.</param>
        public static string FormatLogMessage(
            DateTime timestamp,
            Severity severity,
            LoggerType loggerType,
            string caller,
            string message,
            IPEndPoint myIPEndPoint,
            Exception exception,
            int errorCode)
        {
            return FormatLogMessage_Impl(timestamp, severity, loggerType, caller, message, myIPEndPoint, exception, errorCode, true);
        }

        private static string FormatLogMessage_Impl(
            DateTime timestamp,
            Severity severity,
            LoggerType loggerType,
            string caller,
            string message,
            IPEndPoint myIPEndPoint,
            Exception exception,
            int errorCode,
            bool includeStackTrace)
        {
            if (severity == Severity.Error)
                message = "!!!!!!!!!! " + message;

            string ip = myIPEndPoint == null ? String.Empty : myIPEndPoint.ToString();
            if (loggerType.Equals(LoggerType.Grain))
            {
                // Grain identifies itself, so I don't want an additional long string in the prefix.
                // This is just a temporal solution to ease the dev. process, can remove later.
                ip = String.Empty;
            }
            string exc = includeStackTrace ? LogFormatter.PrintException(exception) : LogFormatter.PrintExceptionWithoutStackTrace(exception);
            string msg = String.Format("[{0} {1,5}\t{2}\t{3}\t{4}\t{5}]\t{6}\t{7}",
                LogManager.ShowDate ? LogFormatter.PrintDate(timestamp) : LogFormatter.PrintTime(timestamp),            //0
                Thread.CurrentThread.ManagedThreadId,   //1
                LogManager.SeverityTable[(int)severity],    //2
                errorCode,                              //3
                caller,                                 //4
                ip,                                     //5
                message,                                //6
                exc);      //7

            return msg;
        }
    }
}
