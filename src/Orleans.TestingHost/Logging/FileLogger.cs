using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.TestingHost.Logging
{
    /// <summary>
    /// The log output which all <see cref="FileLogger"/> share to log messages to 
    /// </summary>
    public class FileLoggingOutput : IDisposable
    {
        private static readonly ConcurrentDictionary<FileLoggingOutput, FileLoggingOutput> Instances = new ConcurrentDictionary<FileLoggingOutput, FileLoggingOutput>();
        private readonly TimeSpan flushInterval = Debugger.IsAttached ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromSeconds(1);
        private readonly object lockObj = new object();
        private readonly string logFileName;
        private DateTime lastFlush = DateTime.UtcNow;
        private StreamWriter logOutput;

        static FileLoggingOutput()
        {
            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;

            static void CurrentDomain_ProcessExit(object sender, EventArgs args)
            {
                foreach (var indstance in Instances.Keys.ToList())
                {
                    indstance.Dispose();
                }
            }
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="fileName"></param>
        public FileLoggingOutput(string fileName)
        {
            this.logFileName = fileName;
            logOutput = new StreamWriter(File.Open(fileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
            Instances[this] = this;
        }

        /// <summary>
        /// Log message for <see cref="FileLogger"/> instance whose category is <paramref name="category"/>
        /// </summary>
        /// <typeparam name="TState"></typeparam>
        /// <param name="logLevel"></param>
        /// <param name="eventId"></param>
        /// <param name="state"></param>
        /// <param name="exception"></param>
        /// <param name="formatter"></param>
        /// <param name="category"></param>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter, string category)
        {
            var logMessage = FormatMessage(DateTime.UtcNow, logLevel, category, formatter(state, exception), exception, eventId);
            lock (this.lockObj)
            {
                if (this.logOutput == null) return;

                this.logOutput.WriteLine(logMessage);
                var now = DateTime.UtcNow;
                if (now - this.lastFlush > flushInterval)
                {
                    this.lastFlush = now;
                    this.logOutput.Flush();
                }
            }
        }

        private static string FormatMessage(
            DateTime timestamp,
            LogLevel logLevel,
            string caller,
            string message,
            Exception exception,
            EventId errorCode)
        {
            if (logLevel == LogLevel.Error)
                message = "!!!!!!!!!! " + message;

            var exc = LogFormatter.PrintException(exception);
            var msg = string.Format("[{0} {1}\t{2}\t{3}\t{4}]\t{5}\t{6}",
                LogFormatter.PrintDate(timestamp),           //0
                Thread.CurrentThread.ManagedThreadId,   //1
                logLevel.ToString(),    //2
                errorCode.ToString(),                              //3
                caller,                                 //4
                message,                                //5
                exc);      //6

            return msg;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            try
            {
                lock (this.lockObj)
                {
                    if (this.logOutput is StreamWriter output)
                    {
                        this.logOutput = null;
                        _ = Instances.TryRemove(this, out _);

                        // Dispose the output, which will flush all buffers.
                        output.Dispose();
                    }
                }
            }
            catch (Exception exc)
            {
                var msg = string.Format("Ignoring error closing log file {0} - {1}", this.logFileName,
                    LogFormatter.PrintException(exc));
                Console.WriteLine(msg);
            }
        }
    }

    /// <summary>
    /// File logger, which logs messages to a file.
    /// </summary>
    public class FileLogger : ILogger
    {
        private readonly FileLoggingOutput output;
        private string category;

        /// <summary>
        /// Constructor
        /// </summary>
        public FileLogger(FileLoggingOutput output, string category)
        {
            this.category = category;
            this.output = output;
        }

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state)
        {
            return NullScope.Instance;
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            this.output.Log(logLevel, eventId, state, exception, formatter, this.category);

        }

        private class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new NullScope();

            private NullScope()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
