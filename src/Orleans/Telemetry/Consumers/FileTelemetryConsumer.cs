using System;
using System.Collections.Generic;
using System.IO;

namespace Orleans.Runtime
{
    public class FileTelemetryConsumer : ITraceTelemetryConsumer
    {
        private StreamWriter _logOutput;
        private readonly object _lockObj = new object();
        private string _logFileName;

        public FileTelemetryConsumer(string fileName) : this(new FileInfo(fileName))
        {
        }

        public FileTelemetryConsumer(FileInfo file)
        {
            _logFileName = file.FullName;
            var fileExists = File.Exists(_logFileName);
            _logOutput = fileExists ? file.AppendText() : file.CreateText();
            file.Refresh();
        }

        public void TrackTrace(string message)
        {
            lock (_lockObj)
            {
                if (_logOutput == null) return;

                _logOutput.WriteLine(message);
            }
        }

        public void TrackTrace(string message, IDictionary<string, string> properties)
        {
            TrackTrace(TraceParserUtils.PrintProperties(message, properties));
        }

        public void TrackTrace(string message, Severity severity)
        {
            TrackTrace(message);
        }

        public void TrackTrace(string message, Severity severity, IDictionary<string, string> properties)
        {
            TrackTrace(message, properties);
        }

        public void Flush()
        {
            lock (_lockObj)
            {
                if (_logOutput == null) return;

                _logOutput.Flush();
            }
        }

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
