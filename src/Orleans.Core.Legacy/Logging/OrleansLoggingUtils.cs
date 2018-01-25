using Orleans.Runtime;
using System;
using System.Net;
using System.Threading;

namespace Orleans.Logging.Legacy
{
    /// <summary>
    /// Utility method for OrleansLogging
    /// </summary>
    public class OrleansLoggingUtils
    {
        internal const string ObsoleteMessageStringForLegacyLoggingInfrastructure = "Deprecated in favor of Microsoft.Extension.Logging";
        internal const string ObsoleteMessageString =
                "The Microsoft.Orleans.Logging.Legacy namespace was kept to facilitate migration from Orleans 1.x but will be removed in the near future. It is recommended that you use the Microsoft.Extensions.Logging infrastructure and providers directly instead of Microsoft.Orleans.Logging.Legacy.Logger and Microsoft.Orleans.Logging.Legacy.ILogConsumer";
        /// <summary>The method to call during logging to format the log info into a string, which is orleans legacy logging style</summary>
        /// <param name="timestamp">Timestamp of the log message.</param>
        /// <param name="severity">The severity of the message being traced.</param>
        /// <param name="loggerType">The type of logger the message is being traced through.</param>
        /// <param name="caller">The name of the logger tracing the message.</param>
        /// <param name="myIPEndPoint">The <see cref="IPEndPoint"/> of the Orleans client/server if known. May be null.</param>
        /// <param name="message">The message to log.</param>
        /// <param name="exception">The exception to log. May be null.</param>
        /// <param name="errorCode">Numeric event code for this log entry. May be zero, meaning 'Unspecified'.</param>
        /// <param name="includeStackTrace">determine include stack trace or not</param>
        /// <returns></returns>
        [Obsolete(ObsoleteMessageString)]
        public static string FormatLogMessageToLegacyStyle(
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
            string exc = includeStackTrace ? LogFormatter.PrintException(exception) : LogFormatter.PrintExceptionWithoutStackTrace(exception);
            string msg = String.Format("[{0} {1,5}\t{2}\t{3}\t{4}\t{5}]\t{6}\t{7}",
                LogFormatter.PrintDate(timestamp),           //0
                Thread.CurrentThread.ManagedThreadId,   //1
                severity.ToString(),    //2
                errorCode,                              //3
                caller,                                 //4
                ip,                                     //5
                message,                                //6
                exc);      //7

            return msg;
        }
    }
}
