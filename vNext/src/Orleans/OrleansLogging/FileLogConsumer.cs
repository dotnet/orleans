using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Orleans.Extensions.Logging
{
    /// <summary>
    /// FileLogConsumer, which logs message into a file in orleans logging message style
    /// </summary>
    public class FileLogConsumer : ICloseableLogConsumer, IFlushableLogConsumer
    {
        private StreamWriter logOutput;
        private readonly object lockObj = new object();
        private string logFileName;
        private IPEndPoint myIpEndPoint;
        public FileLogConsumer(string fileName, IPEndPoint ipEndpoint) :this(new FileInfo(fileName), ipEndpoint)
        {
        }

        public FileLogConsumer(FileInfo file, IPEndPoint ipEndpoint)
        {
            logFileName = file.FullName;
            var fileExists = File.Exists(logFileName);
            logOutput = fileExists ? file.AppendText() : file.CreateText();
            file.Refresh();
            this.myIpEndPoint = ipEndpoint;
        }

        public void Log(Severity severity, string caller, string message, Exception exception, int eventCode = 0)
        {
            var logMessage = FormatLogMessage(DateTime.UtcNow, severity, caller, message, this.myIpEndPoint, exception, eventCode, true);
            lock (this.lockObj)
            {
                if (this.logOutput == null) return;

                this.logOutput.WriteLine(message);
            }
        }

        public void Close()
        {
            if (this.logOutput == null) return; // was already closed.

            try
            {
                lock (this.lockObj)
                {
                    if (this.logOutput == null) // was already closed.
                    {
                        return;
                    }
                    this.logOutput.Flush();
                    this.logOutput.Dispose();
                    this.logOutput = null;
                }
            }
            catch (Exception exc)
            {
                var msg = string.Format("Ignoring error closing log file {0} - {1}", this.logFileName,
                    LogFormatter.PrintException(exc));
                Console.WriteLine(msg);
            }
            finally
            {
                this.logOutput = null;
                this.logFileName = null;
            }
        }

        public void Flush()
        {
            lock (this.lockObj)
            {
                if (this.lockObj == null) return;

                this.logOutput.Flush();
            }
        }


        /// <summary>
        /// Format messgaed in orleans logging style
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
        private static string FormatLogMessage(
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
