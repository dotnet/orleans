using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Orleans.Runtime
{
    /// <summary>
    /// <see cref="Orleans.Runtime.ITelemetryConsumer" /> implementation which writes output to a file.
    /// </summary>
    /// <seealso cref="Orleans.Runtime.ITraceTelemetryConsumer" />
    public class FileTelemetryConsumer : ITraceTelemetryConsumer
    {
        private StreamWriter _logOutput;
        private readonly object _lockObj = new object();
        private string _logFileName;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileTelemetryConsumer"/> class.
        /// </summary>
        /// <param name="fileName">Name of the file.</param>
        public FileTelemetryConsumer(string fileName) : this(new FileInfo(fileName))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FileTelemetryConsumer"/> class.
        /// </summary>
        /// <param name="file">The file.</param>
        public FileTelemetryConsumer(FileInfo file)
        {
            _logFileName = file.FullName;
            _logOutput = new StreamWriter(File.Open(_logFileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
        }

        /// <inheritdoc/>
        public void TrackTrace(string message)
        {
            lock (_lockObj)
            {
                if (_logOutput == null) return;

                _logOutput.WriteLine(message);
            }
        }

        /// <inheritdoc/>
        public void TrackTrace(string message, IDictionary<string, string> properties)
        {
            TrackTrace(TraceParserUtils.PrintProperties(message, properties));
        }

        /// <inheritdoc/>
        public void TrackTrace(string message, Severity severity)
        {
            TrackTrace(message);
        }

        /// <inheritdoc/>
        public void TrackTrace(string message, Severity severity, IDictionary<string, string> properties)
        {
            TrackTrace(message, properties);
        }

        /// <inheritdoc/>
        public void Flush()
        {
            lock (_lockObj)
            {
                if (_logOutput == null) return;

                _logOutput.Flush();
            }
        }

        /// <inheritdoc/>
        public void Close()
        {
            if (_logOutput == null) return; // was already closed.

            try
            {
                lock (_lockObj)
                {
                    if (_logOutput == null) // was already closed.
                    {
                        return;
                    }
                    _logOutput.Flush();
                    _logOutput.Dispose();
                    _logOutput = null;
                }
            }
            catch (Exception exc)
            {
                var msg = string.Format("Ignoring error closing log file {0} - {1}", _logFileName,
                    LogFormatter.PrintException(exc));
                Console.WriteLine(msg);
            }
            finally
            {
                _logOutput = null;
                _logFileName = null;
            }
        }
    }
}
