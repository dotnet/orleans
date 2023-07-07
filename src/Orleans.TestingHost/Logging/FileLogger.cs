using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
        private static readonly ConcurrentDictionary<FileLoggingOutput, object> Instances = new();
        private readonly TimeSpan flushInterval = Debugger.IsAttached ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromSeconds(1);
        private readonly object lockObj = new object();
        private readonly string logFileName;
        private DateTime lastFlush = DateTime.UtcNow;
        private StreamWriter logOutput;

        /// <summary>
        /// Initializes static members of the <see cref="FileLoggingOutput"/> class.
        /// </summary>
        static FileLoggingOutput()
        {
            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;

            static void CurrentDomain_ProcessExit(object sender, EventArgs args)
            {
                foreach (var instance in Instances)
                {
                    instance.Key.Dispose();
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FileLoggingOutput"/> class.
        /// </summary>
        /// <param name="fileName">Name of the log file.</param>
        public FileLoggingOutput(string fileName)
        {
            this.logFileName = fileName;
            logOutput = new StreamWriter(File.Open(fileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
            Instances[this] = null;
        }

        /// <summary>
        /// Logs a message.
        /// </summary>
        /// <typeparam name="TState">The type of <paramref name="state"/>.</typeparam>
        /// <param name="logLevel">The log level.</param>
        /// <param name="eventId">The event identifier.</param>
        /// <param name="state">The state.</param>
        /// <param name="exception">The exception.</param>
        /// <param name="formatter">The formatter.</param>
        /// <param name="category">The category.</param>
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

        /// <inheritdoc />
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
        private readonly string category;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileLogger"/> class.
        /// </summary>
        /// <param name="output">The output logger.</param>
        /// <param name="category">The category.</param>
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
