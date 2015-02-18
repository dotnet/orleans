using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LogicAnalyzerBusinessLogic;
using System.Threading.Tasks;
using System.IO;
namespace LogAnalyzerConsoleApp
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                ProccessCommandLineArgements processArgs = new ProccessCommandLineArgements();
                try
                {
                    processArgs.ParseCommandLineArguments(args);
                    if (processArgs.FileNames.Length > 0)
                    {
                        LogicAnalyzerBusinessLogic.LogFileProcessor logicAnalyzer = new LogicAnalyzerBusinessLogic.LogFileProcessor();
                        //logicAnalyzer.OnProcessLogEntry += new Action<string, int>(logicAnalyzer_OnProcessLogEntry);
                        logicAnalyzer.OnCompletion += new Action<string, int, int>(logicAnalyzer_OnCompletion);
                        logicAnalyzer.OnStart += new Action<string>(logicAnalyzer_OnStart);
                        logicAnalyzer.LogFilter = new FilterCriteria { filterFlag = processArgs.FilterLevel, startInterval = (processArgs.StartInterval != processArgs.EndInterval) ? processArgs.StartInterval : DateTime.MinValue, endInterval = (processArgs.StartInterval != processArgs.EndInterval) ? processArgs.EndInterval : DateTime.MaxValue };
                        DateTime startTime = DateTime.Now;
                        Console.WriteLine("Starting processing...");
                        System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource();
                        int totalRecords = logicAnalyzer.ProcessLogFiles(processArgs.FileNames, processArgs.MaxRecords, cts.Token);
                        Console.WriteLine(string.Format("Finished processing {0} log files, total records processed is {1}", processArgs.FileNames.Length, totalRecords));
                        Console.WriteLine("Generating Summary to file {0}...", processArgs.XmlFileName);
                        using (System.IO.StreamWriter writer = new System.IO.StreamWriter(processArgs.XmlFileName, false, Encoding.Unicode))
                        {
                            writer.Write(logicAnalyzer.SummaryResults);
                        }
                        Console.WriteLine("Completed generating file summary");
                        Console.WriteLine(string.Format("Processing completed, total processing time (secs) is {0}", (DateTime.Now - startTime).TotalSeconds));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("An exception was encountered attempting to parse command line arguments, please make sure all file paths are enclosed in quotes");
                }
            }
            else
            {
                Console.WriteLine("Usage:LogFileConsoleApp.exe [LogFile,,] [MaxRecords=999] [FilterLevel=Error|Warning|Info|Verbose] [StartDate=99/99/9999] [EndDate=99/99/9999] [OutputFile=xmlfilename");
            }
        }

        static void logicAnalyzer_OnStart(string obj)
        {
            Console.WriteLine(string.Format("Started processing log file {0}", obj));
        }

        static void logicAnalyzer_OnCompletion(string obj, int count, int totalProcessed)
        {
            Console.WriteLine(string.Format("Completed processing log file {0} successfully processed {1} records", obj, count));
        }

        static void logicAnalyzer_OnProcessLogEntry(string arg1, int arg2)
        {
        }


        public class ProccessCommandLineArgements
        {
            public ProccessCommandLineArgements()
            {
                MaxRecords = -1;
                FilterLevel = FilterFlag.WARNING | FilterFlag.ERROR;
                XmlFileName = "logSummary.xml";
            }

            public string XmlFileName
            {
                get;
                set;
            }

            public int MaxRecords
            {
                get;
                set;
            }

            public string[] FileNames
            {
                get;
                set;
            }

            public DateTime StartInterval
            {
                get;
                set;
            }

            public DateTime EndInterval
            {
                get;
                set;
            }

            public FilterFlag FilterLevel
            {
                get;
                set;
            }

            void AddFile(List<string> fileNames, string fileName)
            {
                if (System.IO.File.Exists(fileName))
                {
                    fileNames.Add(fileName);
                }
                else
                {
                    throw new System.IO.FileNotFoundException(fileName);
                }
            }

            public void ParseCommandLineArguments(string[] args)
            {
                List<string> fileNames = new List<string>();
                if (args.Length > 0)
                {
                    foreach (string arg in args)
                    {
                        if (!arg.Contains('='))
                        {
                            if (arg.Contains('*'))
                            {
                                string directory = Path.GetDirectoryName(arg);
                                if (string.IsNullOrEmpty(directory))
                                {
                                    directory = ".";
                                }
                                string seachPattern = Path.GetFileName(arg);
                                string[] files = System.IO.Directory.GetFiles(directory, seachPattern, SearchOption.AllDirectories);
                                foreach (string fileName in files)
                                {
                                    AddFile(fileNames, fileName);
                                }
                            }
                            else
                            {
                                AddFile(fileNames, arg);
                            }
                        }
                        else
                        {
                            string[] nameValuePair = arg.Split(new char[] { '=', '/' });
                            if (nameValuePair.Length == 2)
                            {

                                switch (nameValuePair[0].ToUpper())
                                {
                                    case "STARTDATE":
                                        DateTime startInterval;
                                        if (DateTime.TryParse(nameValuePair[1], out startInterval))
                                        {
                                            StartInterval = startInterval;
                                        }
                                        break;
                                    case "ENDDATE":
                                        DateTime endInterval;
                                        if (DateTime.TryParse(nameValuePair[1], out endInterval))
                                        {
                                            EndInterval = endInterval;
                                        }
                                        break;
                                    case "FILTERLEVEL":
                                        FilterFlag filterLevel = 0;
                                        string[] filterFlags = nameValuePair[1].Split('|');
                                        foreach (string filterFlag in filterFlags)
                                        {
                                            switch (filterFlag)
                                            {
                                                case "ERROR":
                                                    filterLevel |= FilterFlag.ERROR;
                                                    break;
                                                case "WARNING":
                                                    filterLevel |= FilterFlag.WARNING;
                                                    break;
                                                case "INFO":
                                                    filterLevel |= FilterFlag.INFO;
                                                    break;
                                                case "VERBOSE":
                                                    filterLevel |= FilterFlag.VERBOSE;
                                                    break;
                                                case "OFF":
                                                    filterLevel = FilterFlag.OFF;
                                                    break;
                                                default:
                                                    break;

                                            }
                                        }
                                        FilterLevel = filterLevel;
                                        break;
                                    case "MAXRECORDS":
                                        MaxRecords = Convert.ToInt32(nameValuePair[1]);
                                        break;
                                    case "OUTPUTFILE":
                                        XmlFileName = nameValuePair[1];
                                        break;
                                }
                            }
                            else
                            {
                                throw new Exception(string.Format("Parsing Error, missing argument for name value pair {0}", nameValuePair));
                            }
                        }
                    }

                    FileNames = fileNames.ToArray();
                }
                else
                {
                    Console.WriteLine("Usage:LogFileConsoleApp.exe [LogFile,,] [MaxRecords=999] [FilterLevel=Error|Warning|Info|Verbose] [StartDate=99/99/9999] [EndDate=99/99/9999] [OutputFile=xmlfilename");
                }
            }
        }
    }
}
