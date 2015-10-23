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
    /// The Log Writer class is a wrapper around the .Net Console class.
    /// </summary>
    public class LogWriterToConsole : LogWriterBase
    {
        private readonly bool useCompactConsoleOutput;

        private readonly string logFormat;

        /// <summary>
        /// Default constructor
        /// </summary>
        public LogWriterToConsole()
            : this(false, false)
        {
        }
        /// <summary>
        /// Constructor which allow some limited overides to the format of log message output,
        /// primarily intended for allow simpler Console screen output.
        /// </summary>
        /// <param name="useCompactConsoleOutput"></param>
        /// <param name="showMessageOnly"></param>
        internal LogWriterToConsole(bool useCompactConsoleOutput, bool showMessageOnly)
        {
            this.useCompactConsoleOutput = useCompactConsoleOutput;

            if (useCompactConsoleOutput)
            {
                // Log format items:
                // {0} = timestamp
                // {1} = severity
                // {2} = errorCode
                // {3} = caller
                // {4} = message
                // {5} = exception

                this.logFormat = showMessageOnly ? "{4}  {5}" : "{0} {1} {2} {3} - {4}\t{5}";
            }
        }

        /// <summary>Format the log message into the format used by this log.</summary>
        protected override string FormatLogMessage(
            DateTime timestamp,
            Logger.Severity severity,
            TraceLogger.LoggerType loggerType,
            string caller,
            string message,
            IPEndPoint myIPEndPoint,
            Exception exception,
            int errorCode)
        {
            // Don't include stack trace in Warning messages for Console output.
            bool includeStackTrace = severity <= Logger.Severity.Error;
            if (!useCompactConsoleOutput)
            {
                return base.FormatLogMessage_Impl(timestamp, severity, loggerType, caller, message, myIPEndPoint, exception, errorCode, includeStackTrace);
            }

            string msg = String.Format(logFormat,
                TraceLogger.PrintTime(timestamp),            //0
                TraceLogger.SeverityTable[(int) severity],    //1
                errorCode,                              //2
                caller,                                 //3
                message,                                //4
                includeStackTrace ? TraceLogger.PrintException(exception) : TraceLogger.PrintExceptionWithoutStackTrace(exception));      //5

            return msg;
        }

        /// <summary>Write the log message for this log.</summary>
        protected override void WriteLogMessage(string msg, Logger.Severity severity)
        {
            Console.WriteLine(msg);
        }
    }
}