using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;

namespace Orleans.Extensions.Logging
{
    /// <summary>
    /// Utility method for OrleansLogging
    /// </summary>
    public class OrleansLoggingUtils
    {
        /// <summary>
        /// Format message in orleans logging style
        /// </summary>
        /// <param name="timestamp"></param>
        /// <param name="severity"></param>
        /// <param name="caller"></param>
        /// <param name="message"></param>
        /// <param name="myIPEndPoint"></param>
        /// <param name="exception"></param>
        /// <param name="errorCode"></param>
        /// <param name="includeStackTrace"></param>
        /// <returns></returns>
        public static string FormatLogMessage(
           DateTime timestamp,
           Severity severity,
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
