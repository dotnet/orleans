using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Xml.Serialization;

namespace LogicAnalyzerBusinessLogic
{
    public enum FilterFlag  {ERROR = 1, WARNING = 2, INFO = 4, VERBOSE = 8, OFF = 16}
    [Serializable]
    public class LogRecord
    {
        [XmlElementAttribute]
        public string logFilePath { get; set; }
        [XmlElementAttribute]
        public int linePosition { get; set; }
        public DateTime logTime { get; set; }
        public string caller { get; set; }
        public FilterFlag traceLevel { get; set; }
        public int threadId { get; set; }
        public string message { get; set; }
        public string exceptionInfo { get; set; }
        public long filePosition;
    }

    public class FilterCriteria
    {
        public DateTime startInterval { get; set; }
        public DateTime endInterval {get;set;}
        public bool SortByDateTime { get; set; }
        public FilterFlag filterFlag { get; set; }
    }

    public interface ILogFileProcessor
    {
        int ProcessLogFiles(string[] logPaths, int maxRecrods, System.Threading.CancellationToken token);
        FilterCriteria LogFilter { get; set; }
        LogRecord[] LogRecords { get; }
        string SummaryResults { get; }
        event Action<string, int> OnProcessLogEntry;
        event Action<string, int, int> OnCompletion;
        event Action<string> OnStart;
    }
}
