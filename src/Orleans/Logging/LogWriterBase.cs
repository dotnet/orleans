/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

namespace Orleans.Runtime
{
    /// <summary>
    /// The Log Writer base class provides default partial implementation suitable for most specific log writer.
    /// </summary>
    public abstract class LogWriterBase : ILogConsumer
    {
        /// <summary>
        /// The method to call during logging.
        /// This method should be very fast, since it is called synchronously during Orleans logging.
        /// </summary>
        /// <remarks>
        /// To customize functionality in a log writter derived from this base class, 
        /// you should override the <c>FormatLogMessage</c> and/or <c>WriteLogMessage</c> 
        /// methods rather than overriding this method directly.
        /// </remarks>
        /// <seealso cref="FormatLogMessage"/>
        /// <seealso cref="WriteLogMessage"/>
        /// <param name="severity">The severity of the message being traced.</param>
        /// <param name="loggerType">The type of logger the message is being traced through.</param>
        /// <param name="caller">The name of the logger tracing the message.</param>
        /// <param name="myIPEndPoint">The <see cref="IPEndPoint"/> of the Orleans client/server if known. May be null.</param>
        /// <param name="message">The message to log.</param>
        /// <param name="exception">The exception to log. May be null.</param>
        /// <param name="eventCode">Numeric event code for this log entry. May be zero, meaning 'Unspecified'.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public void Log(
            Logger.Severity severity,
            TraceLogger.LoggerType loggerType,
            string caller,
            string message,
            IPEndPoint myIPEndPoint,
            Exception exception,
            int errorCode)
        {
            var now = DateTime.UtcNow;

            var msg = FormatLogMessage(
                now,
                severity,
                loggerType,
                caller,
                message,
                myIPEndPoint,
                exception,
                errorCode);

            try
            {
                WriteLogMessage(msg, severity);
            }
            catch (Exception exc)
            {
                Trace.TraceError("Error writing log message {0} -- Exception={1}", msg, exc);
            }
        }

        /// <summary>
        /// The method to call during logging to format the log info into a string ready for output.
        /// </summary>
        /// <param name="severity">The severity of the message being traced.</param>
        /// <param name="loggerType">The type of logger the message is being traced through.</param>
        /// <param name="caller">The name of the logger tracing the message.</param>
        /// <param name="myIPEndPoint">The <see cref="IPEndPoint"/> of the Orleans client/server if known. May be null.</param>
        /// <param name="message">The message to log.</param>
        /// <param name="exception">The exception to log. May be null.</param>
        /// <param name="eventCode">Numeric event code for this log entry. May be zero, meaning 'Unspecified'.</param>
        protected virtual string FormatLogMessage(
            DateTime timestamp,
            Logger.Severity severity,
            TraceLogger.LoggerType loggerType,
            string caller,
            string message,
            IPEndPoint myIPEndPoint,
            Exception exception,
            int errorCode)
        {
            return FormatLogMessage_Impl(timestamp, severity, loggerType, caller, message, myIPEndPoint, exception, errorCode, true);
        }

        protected string FormatLogMessage_Impl(
            DateTime timestamp,
            Logger.Severity severity,
            TraceLogger.LoggerType loggerType,
            string caller,
            string message,
            IPEndPoint myIPEndPoint,
            Exception exception,
            int errorCode,
            bool includeStackTrace)
        {
            if (severity == Logger.Severity.Error)
                message = "!!!!!!!!!! " + message;

            string ip = myIPEndPoint == null ? String.Empty : myIPEndPoint.ToString();
            if (loggerType.Equals(TraceLogger.LoggerType.Grain))
            {
                // Grain identifies itself, so I don't want an additional long string in the prefix.
                // This is just a temporal solution to ease the dev. process, can remove later.
                ip = String.Empty;
            }
            string exc = includeStackTrace ? TraceLogger.PrintException(exception) : TraceLogger.PrintExceptionWithoutStackTrace(exception);
            string msg = String.Format("[{0} {1,5}\t{2}\t{3}\t{4}\t{5}]\t{6}\t{7}",
                TraceLogger.ShowDate ? TraceLogger.PrintDate(timestamp) : TraceLogger.PrintTime(timestamp),            //0
                Thread.CurrentThread.ManagedThreadId,   //1
                TraceLogger.SeverityTable[(int) severity],    //2
                errorCode,                              //3
                caller,                                 //4
                ip,                                     //5
                message,                                //6
                exc);      //7

            return msg;
        }

        /// <summary>
        /// The method to call during logging to write the log message by this log.
        /// </summary>
        /// <param name="msg">Message string to be writter</param>
        /// <param name="severity">The severity level of this message</param>
        protected abstract void WriteLogMessage(string msg, Logger.Severity severity);
    }
}