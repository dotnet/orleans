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
            var logMessage = OrleansLoggingUtils.FormatLogMessage(DateTime.UtcNow, severity, caller, message, this.myIpEndPoint, exception, eventCode, true);
            lock (this.lockObj)
            {
                if (this.logOutput == null) return;

                this.logOutput.WriteLine(logMessage);
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
    }
}
