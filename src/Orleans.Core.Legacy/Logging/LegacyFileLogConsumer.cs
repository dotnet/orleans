using Orleans.Runtime;
using System;
using System.IO;
using System.Net;
using System.Text;

namespace Orleans.Logging.Legacy
{
    /// <summary>
    /// LegacyFileLogConsumer, which logs message into a file in orleans logging message style
    /// </summary>
    public class LegacyFileLogConsumer : ICloseableLogConsumer, IFlushableLogConsumer
    {
        private StreamWriter logOutput;
        private readonly object lockObj = new object();
        private string logFileName;

        public LegacyFileLogConsumer(string fileName)
        {
            this.logFileName = fileName;
            logOutput = new StreamWriter(File.Open(fileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
        }

        public void Log(
            Severity severity,
            LoggerType loggerType,
            string caller,
            string message,
            IPEndPoint ipEndPoint,
            Exception exception,
            int eventCode = 0
        )
        {
            var logMessage = OrleansLoggingUtils.FormatLogMessageToLegacyStyle(DateTime.UtcNow, severity, loggerType, caller, message, ipEndPoint, exception, eventCode, true);
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
                this.logOutput.Flush();
            }
        }
    }
}
