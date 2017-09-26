﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions.Internal;
using Orleans.Runtime;

namespace Orleans.Logging
{
    /// <summary>
    /// The log output which all <see cref="FileLogger"/> share to log messages to 
    /// </summary>
    public class FileLoggingOutput 
    {
        private readonly TimeSpan flushInterval = Debugger.IsAttached ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromSeconds(1);
        private DateTime lastFlush = DateTime.UtcNow;
        private StreamWriter logOutput;
        private readonly object lockObj = new object();
        private string logFileName;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="fileName"></param>
        public FileLoggingOutput(string fileName)
        {
            logOutput = new StreamWriter(File.Open(fileName, FileMode.Append, FileAccess.Write, FileShare.Write));
            this.logFileName = fileName;
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

            string exc = LogFormatter.PrintException(exception);
            string msg = String.Format("[{0} {1}\t{2}\t{3}\t{4}]\t{5}\t{6}",
                LogFormatter.PrintDate(timestamp),           //0
                Thread.CurrentThread.ManagedThreadId,   //1
                logLevel.ToString(),    //2
                errorCode.ToString(),                              //3
                caller,                                 //4
                message,                                //5
                exc);      //6

            return msg;
        }

        /// <summary>
        /// Close the output
        /// </summary>
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

        /// <inheritdoc cref="ILogger"/>
        public IDisposable BeginScope<TState>(TState state)
        {
            //TODO: implemente scope 
            return NullScope.Instance;
        }
        /// <inheritdoc cref="ILogger"/>
        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }
        /// <inheritdoc cref="ILogger"/>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            this.output.Log(logLevel, eventId, state, exception, formatter, this.category);

        }
    }
}
