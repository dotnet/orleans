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
    /// This Log Writer class is an Orleans Log Consumer wrapper class which writes to a specified log file.
    /// </summary>
    public class LogWriterToFile : LogWriterBase, IFlushableLogConsumer, ICloseableLogConsumer
    {
        private string logFileName;
        private readonly bool useFlush;
        private StreamWriter logOutput;
        private readonly object lockObj = new Object();

        /// <summary>
        /// Constructor, specifying the file to send output to.
        /// </summary>
        /// <param name="logFile">The log file to be written to.</param>
        public LogWriterToFile(FileInfo logFile)
        {
            this.logFileName = logFile.FullName;

            bool fileExists = File.Exists(logFileName);
            this.logOutput = fileExists ? logFile.AppendText() : logFile.CreateText();

            this.useFlush = !logOutput.AutoFlush;
            logFile.Refresh(); // Refresh the cached view of FileInfo
        }

        /// <summary>Close this log file, after flushing any pending output.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public void Close()
        {
            if (logOutput == null) return; // was already closed.

            try
            {
                lock (lockObj)
                {
                    if (logOutput == null) // was already closed.
                    {
                        return;
                    }
                    logOutput.Flush();
                    logOutput.Dispose();
                }
            }
            catch (Exception exc)
            {
                string msg = string.Format("Ignoring error closing log file {0} - {1}", logFileName, TraceLogger.PrintException(exc));
                Console.WriteLine(msg);
            }
            this.logOutput = null;
            this.logFileName = null;
        }

        /// <summary>Write the log message for this log.</summary>
        protected override void WriteLogMessage(string msg, Logger.Severity severity)
        {
            lock (lockObj)
            {
                if (logOutput == null) return;
                logOutput.WriteLine(msg);
                if (useFlush)
                {
                    logOutput.Flush(); // We need to explicitly flush each log write
                }
            }
        }

        /// <summary>Flush any pending output for this log.</summary>
        public void Flush()
        {
            lock (lockObj)
            {
                if (logOutput == null) return;
                logOutput.Flush();
            }
        }
    }
}