using System.Collections.Generic;
using System.IO;

namespace Orleans.Runtime
{
    public class FileTelemetryConsumer : ITraceTelemetryConsumer
    {
        private StreamWriter _logOutput;
        private readonly object _lockObj = new object();

        public FileTelemetryConsumer(string fileName) : this(new FileInfo(fileName))
        {
        }

        public FileTelemetryConsumer(FileInfo file)
        {
            var fileExists = File.Exists(file.FullName);
            _logOutput = fileExists ? file.AppendText() : file.CreateText();
            file.Refresh();
        }

        public void TrackTrace(string message)
        {
            lock (_lockObj)
            {
                if (_logOutput == null) return;
                _logOutput.WriteLine(message);
                _logOutput.Flush(); 
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
    }
}
