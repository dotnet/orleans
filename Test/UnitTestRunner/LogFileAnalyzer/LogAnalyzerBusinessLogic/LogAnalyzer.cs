using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.Diagnostics;
using System.Xml;
using System.Xml.Serialization;
using System.Threading;

namespace LogicAnalyzerBusinessLogic
{
    
    public class LogFileProcessor : ILogFileProcessor
    {
        const string DATETIMEFORMAT = "ddd MMM d yyyy, HH:mm:ss:fff";
        const int BufferSize = 4096;
        public event Action<string, int> OnProcessLogEntry;
        public event Action<string, int, int> OnCompletion;
        public event Action<string> OnStart;

        private List<LogRecord> logRecords = new List<LogRecord>();
        private CancellationToken token;
        private int totalRecords;
        public FilterCriteria LogFilter
        {
            get;
            set;
        }

        public int MaxRecords
        {
            get;
            set;
        }

        public LogFileProcessor()
        {
            MaxRecords = -1; // process all records unless told otherwise
        }

        public int ProcessLogFiles(string[] logPaths, int maxRecords, System.Threading.CancellationToken token)
        {
            System.Threading.ThreadPool.SetMinThreads(logPaths.Length, logPaths.Length);
            MaxRecords = maxRecords;
            List<Task> tasks = new List<Task>();
            try
            {
                foreach (string logPath in logPaths)
                {
                    string path = logPath;
                    System.Threading.Tasks.Task task = new Task(() =>
                    {
                        ProcessLogFile(path, token);
                    });

                    tasks.Add(task);
                    task.Start();
                    //ProcessLogFile(logPath);
                }
            }
            catch (OperationCanceledException ex)
            {
                // operation cancelled, we still need to wait for remaining threads to complete
            }
            
            System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
            
            if (OnCompletion != null)
            {
                OnCompletion(null, logRecords.Count, totalRecords);
            }
            return totalRecords;

        }

        public LogRecord[] LogRecords
        {
            get
            {
                
                if (LogFilter != null)
                {
                    var records = from r in logRecords
                                  where r!= null && r.logTime > LogFilter.startInterval && r.logTime < LogFilter.endInterval && LogFilter.filterFlag >= r.traceLevel
                                  orderby r.logTime
                                  select r;

                    return records.ToArray<LogRecord>();
                }
                else
                {
                    return logRecords.ToArray();
                }
            }
        }

        public string SummaryResults
        {
            get
            {
                if (LogFilter.endInterval != LogFilter.startInterval)
                {
                    var records = from r in logRecords
                                  where r != null && r.logTime > LogFilter.startInterval && r.logTime < LogFilter.endInterval
                                  select r;

                    return GenerateSummaryAnalysis(records.ToArray<LogRecord>());
                }
                else
                {
                    return GenerateSummaryAnalysis(logRecords.ToArray());
                }
            }
                 
        }
        protected string GenerateSummaryAnalysis(LogRecord[] records)
        {
            var totalInfos = from r in records
                        where r != null && r.traceLevel == FilterFlag.INFO
                        orderby r.logTime
                        select r;

            var totalWarnings = from r in records
                                where r != null && r.traceLevel == FilterFlag.WARNING
                                orderby r.logTime
                                select r;

            var totalErrors = from r in records
                              where r != null && r.traceLevel == FilterFlag.ERROR
                              orderby r.logTime
                              select r;

            StringBuilder sb = new StringBuilder();
            XmlWriter writer = XmlWriter.Create(sb);
            writer.WriteStartDocument(false);
            writer.WriteStartElement("LogData");
            writer.WriteStartElement("SummaryData");
            writer.WriteAttributeString("Generated", DateTime.Now.ToString(DATETIMEFORMAT));
            writer.WriteAttributeString("MaxRecords", this.MaxRecords.ToString());
            writer.WriteAttributeString("TotalRecordsProcessed", this.totalRecords.ToString());

            if (LogFilter != null)
            {
                writer.WriteAttributeString("FilterLevel", this.LogFilter.filterFlag.ToString());
                writer.WriteAttributeString("StartInterval", LogFilter.startInterval.ToString(DATETIMEFORMAT));
                writer.WriteAttributeString("EndInterval", LogFilter.endInterval.ToString(DATETIMEFORMAT));
            }
            writer.WriteElementString("TotalInfos", totalInfos.Count().ToString());
            writer.WriteElementString("TotalWarnings", totalWarnings.Count().ToString());
            writer.WriteElementString("TotalErrors", totalErrors.Count().ToString());
            writer.WriteStartElement("LogRecords");
            writer.WriteEndElement(); // closes </SummaryData>

            //XmlSerializer serializer = new XmlSerializer(typeof(LogRecord));
            if (totalErrors.Count() > 0)
            {
                writer.WriteStartElement("Errors");
                for (int i = 0; i < 10 && i < totalErrors.Count(); i++)
                {
                    LogRecord r = totalErrors.ElementAt(i);
                    SerializeLogRecord(writer, r);
                    //serializer.Serialize(writer, r);
                }
                writer.WriteEndElement();
            }

            if (totalWarnings.Count() > 0)
            {
                writer.WriteStartElement("Warnings");
                for (int i = 0; i < 10 && i < totalWarnings.Count(); i++)
                {
                    LogRecord r = totalWarnings.ElementAt(i);
                    SerializeLogRecord(writer, r);
                }
                writer.WriteEndElement();
            }

            if (totalInfos.Count() > 0)
            {
                writer.WriteStartElement("Infos");
                for (int i = 0; i < 10 && i < totalInfos.Count(); i++)
                {
                    LogRecord r = totalInfos.ElementAt(i);
                    SerializeLogRecord(writer, r);
                }
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // closes </LogRecords>
            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Close();
            return sb.ToString(); 
        }


        protected void SerializeLogRecord(XmlWriter writer, LogRecord record)
        {
            writer.WriteStartElement("LogRecord");
            foreach (System.Reflection.PropertyInfo info in record.GetType().GetProperties())
            {
                object[] attribs = info.GetCustomAttributes(typeof(XmlElementAttribute), false);
                object obj = null;
                obj = info.GetValue(record, null);
                if (obj != null)
                {
                    switch (obj.GetType().ToString())
                    {
                        case "System.DateTime":
                            writer.WriteElementString(info.Name, ((DateTime)obj).ToString(DATETIMEFORMAT));
                            break;
                        default:
                            if (attribs.Length == 0)
                            {
                                writer.WriteElementString(info.Name, obj.ToString());
                            }
                            else
                            {
                                writer.WriteAttributeString(info.Name, obj.ToString());
                            }
                            break;
                    }
                }
            }
            writer.WriteEndElement();
        }

        protected void ProcessLogFile(string logPath, CancellationToken token)
        {
            string logFileName = System.IO.Path.GetFileName(logPath);
            if (OnStart != null)
            {
                OnStart(logFileName);
            }
            try
            {
                int currentRecord = 0;
                long filePosition = 0;
                using (System.IO.StreamReader strm = new System.IO.StreamReader(logPath))
                {
                    char[] buffer = new char[BufferSize];
                    //string charBuffer = null;
                    LogRecord logRecord = new LogRecord { filePosition = filePosition, linePosition = currentRecord, message = "Click to retrieve message" };
                    //string logEntry = ReadLine(strm, buffer, ref charBuffer, logRecord, currentLine++);
                    string logEntry = strm.ReadLine();
                    while (logEntry != null && (MaxRecords == -1 || currentRecord < MaxRecords) && !token.IsCancellationRequested)
                    {
                        logRecord.logFilePath = logPath;
                        while (logEntry != null && ExtractLogData(logEntry, logRecord, false) == false)
                        {
                            logEntry = strm.ReadLine();
                            filePosition += logEntry.Length;
                        }

                        currentRecord++; // increment line counter
                        if (logRecord.traceLevel <= LogFilter.filterFlag)
                        {
                            logRecords.Add(logRecord);
                        }
                        #region fire event notification that entry has been processed
                        if (currentRecord % 1000 == 0 && OnProcessLogEntry != null)
                        {
                            OnProcessLogEntry(logFileName, currentRecord);
                        }
                        #endregion
                        logRecord = new LogRecord { filePosition = strm.BaseStream.Position, linePosition = currentRecord, message = "Click to retrieve log message" };
                        logEntry = strm.ReadLine();
                        //logEntry = ReadLine(strm, buffer, ref charBuffer, logRecord, currentLine++);
                    }
                    token.ThrowIfCancellationRequested();
                }
                totalRecords += currentRecord;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(string.Format("Error reading log entry for " + logPath + " Error {0}", ex.ToString()));
            }

        }

        protected virtual bool ExtractLogData(string logEntry, LogRecord logRecord, bool bHeaderOnly=true)
        {
            if (logEntry.Length > 0)
            {
                if (logEntry[0] == '[')
                {
                    int index = logEntry.IndexOf(']');
                    if (index != -1)
                    {
                        // extract header data
                        try
                        {
                            string header = logEntry.Substring(0, index + 1);
                            string[] values = header.Split('\t');
                            if (values.Length > 0)
                            {
                                DateTime logTime;
                                if (DateTime.TryParseExact(values[0].TrimStart('['), DATETIMEFORMAT, null, DateTimeStyles.None, out logTime))
                                {
                                    logRecord.logTime = logTime;
                                    if (values.Length > 1)
                                    {
                                        logRecord.threadId = Convert.ToInt32(values[1]);
                                        if (values.Length > 2)
                                        {
                                            switch (values[2].Trim())
                                            {
                                                case "INFO":
                                                    logRecord.traceLevel = FilterFlag.INFO;
                                                    break;
                                                case "ERROR":
                                                    logRecord.traceLevel = FilterFlag.ERROR;
                                                    break;
                                                case "WARNING":
                                                    logRecord.traceLevel = FilterFlag.WARNING;
                                                    break;
                                                case "OFF":
                                                    logRecord.traceLevel = FilterFlag.OFF;
                                                    break;
                                                default:
                                                    logRecord.traceLevel = FilterFlag.VERBOSE;
                                                    break;
                                            }

                                            if (values.Length > 3)
                                            {
                                                logRecord.caller = values[3].TrimEnd(']', ' ');
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    Trace.WriteLine(string.Format("Skipping log record unable to parse {0}", logEntry));
                                    return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine(string.Format("Skipping log record unable to parse {0}", logEntry));
                            return true;
                        }
                    }

                    if (!bHeaderOnly)
                    {
                        #region process content information
                        string content = logEntry.Substring(index + 1).TrimStart('\t');
                        string[] contentValues = content.Split('\t');
                        if (contentValues.Length > 0)
                        {
                            logRecord.message = contentValues[0];
                            if (content.Length == logRecord.message.Length) // indicates we did not encounter a terminating '/t'
                            {
                                return false;
                            }
                            else
                            {
                                logRecord.exceptionInfo = contentValues[1];
                            }
                        }
                        #endregion
                    }
                    return true;
                }
                else
                {
                    if (logRecord.traceLevel == FilterFlag.ERROR)
                    {
                        logRecord.exceptionInfo += logEntry;
                    }
                    else if (logRecord.traceLevel == FilterFlag.WARNING)
                    {
                        logRecord.message += logEntry;
                    }
                    if (logEntry[logEntry.Length - 1] != '\t')
                        return false;
                    else
                        return true;
                }
            }
            return false;
        }
    }
}
