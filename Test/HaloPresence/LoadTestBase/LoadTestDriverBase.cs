using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Remoting;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Orleans;
using Orleans.Runtime;


namespace LoadTestBase
{
    public class LoadTestBaseConfig
    {
        public long NUM_REQUESTS;
        public int NUM_WORKERS;
        public int NUM_THREADS_PER_WORKER;
        public int PIPELINE_SIZE;
        public int GW_PORT;
        public int GW_INSTANCE_INDEX ;
        public bool USE_AZURE_SILO_TABLE;
        public int NUM_REQUESTS_IN_REPORT;
        public int NUM_REPORTS_IN_BLOCK;
        public int NUM_WARMUP_BLOCKS;
        public int TARGET_LOAD_PER_CLIENT;

        public bool DirectClientTest;

        public int NumRequestsInBlock { get { return NUM_REQUESTS_IN_REPORT * NUM_REPORTS_IN_BLOCK; } }
        public readonly List<IPEndPoint> gatewayList = new List<IPEndPoint>();

        private LoadTestBaseConfig() { }

        public override string ToString()
        {
            return String.Format("NUM_REQUESTS = {0}, NUM_WORKERS = {1}, NUM_THREADS_PER_WORKER = {2}, PIPELINE_SIZE = {3}, GW_PORT = {4}, GW_INSTANCE_INDEX = {5}, USE_AZURE_SILO_TABLE = {6}, NUM_WARMUP_BLOCKS = {7} DirectClientTest = {8}",
                    NUM_REQUESTS,
                    NUM_WORKERS,
                    NUM_THREADS_PER_WORKER,
                    PIPELINE_SIZE,
                    GW_PORT,
                    GW_INSTANCE_INDEX,
                    USE_AZURE_SILO_TABLE,
                    NUM_WARMUP_BLOCKS,
                    DirectClientTest
                    );
        }

        public void Validate()
        {
            if ((NUM_REQUESTS % NUM_WORKERS) != 0)
            {
                throw new ArgumentException("NUM_REQUESTS should be devisable by NUM_WORKERS. Config is: " + this);
            }
            if ((NUM_REQUESTS % (NUM_WORKERS * NUM_THREADS_PER_WORKER)) != 0)
            {
                throw new ArgumentException("NUM_REQUESTS should be devisable by NUM_WORKERS * NUM_THREADS_PER_WORKER. Config is: " + this);
            }
            //if ((NUM_REQUESTS % REPORT_BLOCK_SIZE) != 0)
            //{
            //    throw new ArgumentException("NUM_REQUESTS should be devisable by REPORT_BLOCK_SIZE");
            //}
        }

        public static LoadTestBaseConfig GetDefaultConfig()
        {
            LoadTestBaseConfig config = new LoadTestBaseConfig();
            config.NUM_REQUESTS = 100 * 1000 * 1000;
            config.NUM_WORKERS = 5;
            config.NUM_THREADS_PER_WORKER = 1;
            config.PIPELINE_SIZE = 500;
            config.GW_PORT = 30000;
            config.GW_INSTANCE_INDEX = -1;
            config.USE_AZURE_SILO_TABLE = false;
            config.NUM_REQUESTS_IN_REPORT = 5000;
            config.NUM_REPORTS_IN_BLOCK = 10;
            config.NUM_WARMUP_BLOCKS = 0;
            config.DirectClientTest = false;
            config.TARGET_LOAD_PER_CLIENT = 0;
            return config;
        }
    }

    internal class BlockStats
    {
        internal int Successes { get; private set; }
        internal int Failures { get; private set; }
        internal int Late { get; private set; }
        internal int Busy { get; private set; }
        internal int PipelineSize { get; private set; }
        private DateTime startTime;
        private DateTime endTime;
        private float cpuUsageSum;
        private int numCpuUsageRecords;
        internal TimeSpan TotalExecutionTime 
        {
            get { return endTime - startTime; }
        }

        internal string TotalExecutionTimeString
        {
            get { return TimeSpanToString(TotalExecutionTime); }
        }

        internal TimeSpan ExecutionTimeTillNow
        {
            get { return DateTime.UtcNow - startTime; }
        }

        internal BlockStats()
        {
            Successes = 0;
            Failures = 0;
            Late = 0;
            Busy = 0;
            PipelineSize = 0;
            cpuUsageSum = 0;
            numCpuUsageRecords = 0;
        }

        internal void StartBlock()
        {
            startTime = DateTime.UtcNow;
        }

        internal void EndBlock()
        {
            endTime = DateTime.UtcNow;
        }

        internal void Add(BlockStats block)
        {
            Successes += block.Successes;
            Failures += block.Failures;
            Late += block.Late;
            Busy += block.Busy;
            PipelineSize += block.PipelineSize;
            cpuUsageSum += block.cpuUsageSum;
            numCpuUsageRecords += block.numCpuUsageRecords;
        }

        internal void Add(int nSuccess, int nFailures, int nLate, int nBusy, int nPipelineSize)
        {
            Successes += nSuccess;
            Failures += nFailures;
            Late += nLate;
            Busy += nBusy;
            PipelineSize += nPipelineSize;
        }

        internal void RecordCPU(float cpu)
        {
            cpuUsageSum += cpu;
            numCpuUsageRecords++;
        }

        internal float GetAverageCPU()
        {
            if (numCpuUsageRecords == 0) return 0;
            return cpuUsageSum / (float)numCpuUsageRecords;
        }

        internal float GetBlockTPS()
        {
            return ((float)1000.0) * ((float)Successes / (float)TotalExecutionTime.TotalMilliseconds);
        }

        internal float GetTPSTillNow()
        {
            return ((float)1000.0) * ((float)Successes / (float)ExecutionTimeTillNow.TotalMilliseconds);
        }

        public static string TimeSpanToString(TimeSpan span)
        {
            return String.Format("{0}:{1}:{2}.{3}", span.Days * 24 + span.Hours, span.Minutes, span.Seconds, span.Milliseconds);
        }
    }

    public class LoadTestDriverBase
    {
        public LoadTestBaseConfig Config { get; private set; }
        private int nReports;
        private BlockStats blockStats;
        private BlockStats totalStats;
        private BlockStats superBlockStats;
        private PerformanceCounter cpuCounter;
        private static SimpleLogWriterToFile logWriter;
        //private FloatValueStatistic latestTPSStatistic;
        //private FloatValueStatistic totalTPSStatistic;
        private float latestTPS;
        private float totalTPS;

        public DirectClientWorkerBase[] Workers;

        public LoadTestDriverBase(string appName)
        {
            blockStats = new BlockStats();
            totalStats = new BlockStats();
            superBlockStats = new BlockStats();
            cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);

            try
            {
                const string dateFormat = "yyyy-MM-dd-HH.mm.ss.fffZ";
                string logOutputFile = String.Format("..//{0}_{1}_{2}.txt", appName, Dns.GetHostName(), DateTime.UtcNow.ToString(dateFormat));
                logWriter = new SimpleLogWriterToFile(new FileInfo(logOutputFile));
            }
            catch (Exception) { } // just ignore
        }

        private static void AddGatewayEndpoint(string host, LoadTestBaseConfig cfg)
        {
            IPAddress[] addresses = Dns.GetHostAddresses(host);
            foreach (IPAddress address in addresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    IPEndPoint endpoint = new IPEndPoint(address, cfg.GW_PORT);
                    cfg.gatewayList.Add(endpoint);
                    return;
                }
            }

            throw new ApplicationException(String.Format("Cannot resolve {0} to an IPv4 address", host));
        }

        private static string CommandLineHelp()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("USAGE:");
            //sb.AppendLine("    LoadTestDriverBase [-w | -n (# of requests) | -users (# of users)");
            sb.AppendLine("    LoadTestDriverBase [-n (# of requests)");
            sb.AppendLine("                     | -pipe (pipeline size) | -threads (# of worker threads)"); 
            sb.AppendLine("                     | -port (gateway default port)");
            sb.AppendLine("                     | -instanceIndex (index of the instance)] (-gw GatewayEndpoint)*");
            sb.AppendLine("where");
            sb.AppendLine("    GatewayEndpoint                  List of IPs to use for gateways.  This list ");
            sb.AppendLine("                                     is space seperated.  i.e. IP#1 IP#2 ...");
            sb.AppendLine("    Options:");
            sb.AppendLine("       -n                            Number of requests.  Defaults to 100000000.");
            sb.AppendLine("       -pipe or -pipeline or -p      Pipeline size.  Defaults to 500.");
            sb.AppendLine("       -workers or -w                Number of workers.  Defaults to 5.");
            sb.AppendLine("       -threads or -t                Number of threads per worker.  Defaults to 1.");
            sb.AppendLine("       -port                         Gateway's port number. Defaults to 30000.");
            sb.AppendLine("       -instanceIndex                The index number of the Gateway to use ");
            sb.AppendLine("                                     from ClientConfiguration.xml.");
            sb.AppendLine("       -gw                           Gateway.");
            sb.AppendLine("       -testId                       Unique identifier of this test within same run.");
            sb.AppendLine();
            sb.AppendLine("Examples:");
            sb.AppendLine("     > PresenceConsoleTest                       ... Show information");
            sb.AppendLine("     > PresenceConsoleTest MachineName           ... Runs with all defaults");
            sb.AppendLine("                                                     with one gateway.");
            sb.AppendLine("     > PresenceConsoleTest -w MachineName        ... Runs with warm-up.");
            sb.AppendLine("     > PresenceConsoleTest -w -n 1000 -u 50 -w 1 -t 2 -gw MachineName");
            return sb.ToString();
        }

        private static bool ParseBaseArguments(string[] args, LoadTestBaseConfig cfg)
        {
            if (args.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].Trim();

                if (a == "-?" || a == "-help" || a == "/?" || a == "/help")
                {
                    return false;
                }
                else if (a == "-requests" || a == "-n") // number of request defaults: 
                {
                    cfg.NUM_REQUESTS = long.Parse(args[++i]);
                }
                else if (a == "-pipe" || a == "-pipeline" || a == "-p") // pipeline size
                {
                    cfg.PIPELINE_SIZE = int.Parse(args[++i]);
                }
                else if (a == "-workers" || a == "-w") //number of workers
                {
                    cfg.NUM_WORKERS = int.Parse(args[++i]);
                }
                else if (a == "-threads" || a == "-t") //number of threads per worker.
                {
                    cfg.NUM_THREADS_PER_WORKER = int.Parse(args[++i]);
                }
                else if (a == "-port") // Gateway port default: 30000
                {
                    cfg.GW_PORT = int.Parse(args[++i]);
                }
                else if (a == "-instanceIndex")
                {
                    cfg.GW_INSTANCE_INDEX = int.Parse(args[++i]);
                }
                else if (a == "-direct")
                {
                    cfg.DirectClientTest = true;
                }
                else if (a == "-perClientRate")
                {
                    cfg.TARGET_LOAD_PER_CLIENT = int.Parse(args[++i]);
                }
                else if (a == "-azure")
                {
                    cfg.USE_AZURE_SILO_TABLE = true;
                }
                else if (a == "-gw")
                {
                    AddGatewayEndpoint(a, cfg);
                }
            }
            return true;
        }

        public LoadTestBaseConfig InitConfig(string[] args, LoadTestBaseConfig cfg)
        {
            WriteProgress("LoadTestDriverBase starting with cmdline args = {0}  ", LoadTestGrainInterfaces.Utils.EnumerableToString(args));

            if (cfg == null)
            {
                cfg = LoadTestBaseConfig.GetDefaultConfig();
            }

            bool ok = ParseBaseArguments(args, cfg);
            if (!ok)
            {
                WriteProgress(CommandLineHelp());
                return null;
            }
            cfg.Validate();
            Config = cfg;
            return Config;
        }
        public bool Initialze(Type workerClassType)
        {
            WriteProgress("LoadTestDriverBase starting on {0}  with initial Config: {1}", Dns.GetHostName(), Config.ToString());
            using (RegistryKey registry = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
            {
                object isProxyEnabled = registry != null ? registry.GetValue("ProxyEnable") : Boolean.FalseString;
                WriteProgress("LoadTestDriverBase ProxyEnabled={0}", isProxyEnabled);
            }

            var gateways = Config.gatewayList.ToArray();
            string workerClassName = workerClassType.FullName;

            AppDomain[] appDomains = new AppDomain[Config.NUM_WORKERS];
            if (workerClassType.IsSubclassOf(typeof(OrleansClientWorkerBase)))
            {
                Workers = new OrleansClientWorkerBase[Config.NUM_WORKERS];
            }
            else
            {
                Workers = new DirectClientWorkerBase[Config.NUM_WORKERS];
            }
            Callback callback = new Callback(ReportCallback);

            int warmupRequests = Config.NUM_WARMUP_BLOCKS * Config.NumRequestsInBlock;
            List<Task> promises = new List<Task>();
            for (int i = 0; i < Config.NUM_WORKERS; i++)
            {
                int workerIndex = i;
                Task promise = Task.Factory.StartNew(() =>
                    {
                        if (Config.NUM_WORKERS == 1)
                        {
                            appDomains[workerIndex] = AppDomain.CurrentDomain;
                        }
                        else
                        {
                            appDomains[workerIndex] = AppDomain.CreateDomain("Worker" + workerIndex);
                        }
                        Workers[workerIndex] = (DirectClientWorkerBase)appDomains[workerIndex].CreateInstanceAndUnwrap(workerClassType.Assembly.FullName, workerClassName);

                        IPEndPoint gw = null;
                        int myInstanceIndex = -1;
                        if (Config.GW_INSTANCE_INDEX >= 0)
                        {
                            // Use specified gateway from command line
                            myInstanceIndex = Config.GW_INSTANCE_INDEX + workerIndex;
                        }
                        else if (gateways.Length != 0)
                        {
                            // Use specified gateway from command line
                            gw = gateways[workerIndex % gateways.Length];
                        }
                        Workers[workerIndex].Initialize(
                                workerIndex,
                                (Config.NUM_REQUESTS + warmupRequests) / Config.NUM_WORKERS,
                                Config.NUM_THREADS_PER_WORKER,
                                Config.NUM_REQUESTS_IN_REPORT,
                                Config.PIPELINE_SIZE,
                                Config.TARGET_LOAD_PER_CLIENT/ Config.NUM_WORKERS,
                                callback);

                        OrleansClientWorkerBase orleansClientWorker = Workers[workerIndex] as OrleansClientWorkerBase;
                        if (orleansClientWorker != null)
                        {
                            orleansClientWorker.InitializeOrleansClientConnection(
                                  gw,
                                  myInstanceIndex,
                                  Config.USE_AZURE_SILO_TABLE);
                        }

                    });
                promises.Add(promise);
                //Thread.Sleep(10);
            }
            Task.WhenAll(promises).Wait();
            WriteProgress("Done LoadTestDriverBase Initialize by all workers with final config: {0}", Config);
            return true;
        }
       
        public void Run()
        {
            if (Config.NUM_WORKERS == 1)
            {
                //latestTPSStatistic = FloatValueStatistic.FindOrCreate(StatNames.STAT_APP_REQUESTS_TPS_LATEST, () => latestTPS);
                //totalTPSStatistic = FloatValueStatistic.FindOrCreate(StatNames.STAT_APP_REQUESTS_TPS_TOTAL_SINCE_START, () => totalTPS);
            }

            blockStats.StartBlock();
            bool isWarmupNeeded = Config.NUM_WARMUP_BLOCKS > 0;
            if (!isWarmupNeeded)
            {
                superBlockStats.StartBlock();
                totalStats.StartBlock();
            }

            WriteProgress("[{0}] Starting sending {1:#,0} requests with Pipe={2} ...", TraceLogger.PrintDate(DateTime.UtcNow), Config.NUM_REQUESTS, Config.PIPELINE_SIZE);

            Task[] promises = new Task[Config.NUM_WORKERS];

            for (int i = 0; i < Config.NUM_WORKERS; i++)
            {
                int index = i;
                promises[i] = Task.Factory.StartNew(() => Workers[index].Run());
                Thread.Sleep(1 * 1000);
            }
            try
            {
                Task.WhenAll(promises).Wait();
            }
            catch (Exception e)
            {
                WriteProgress("EXCEPTION \n{0}", e);
            }

            totalStats.EndBlock();

            WriteProgress("[{0}] Finished sending {1:#,0} requests", TraceLogger.PrintDate(DateTime.UtcNow), Config.NUM_REQUESTS);
        }

        public void WriteTestResults()
        {
            WriteProgress("Test completed successfully in {0:#,0} milliseconds (TimeSpan of {1}, time now [{2}])",
                totalStats.TotalExecutionTime.TotalMilliseconds, totalStats.TotalExecutionTime, TraceLogger.PrintDate(DateTime.UtcNow));
            //WriteProgress("Average TPS : {0:0.0}", 1000.0 * ((float)totalStats.Successes / totalStats.TotalExecutionTime.TotalMilliseconds));
            WriteProgress("Average TPS : {0:0.0}", totalStats.GetBlockTPS());
            WriteProgress("Total Successes: {0} ({1:0.0##}%), Failures: {2} ({3:0.0##}%), Late: {4} ({5:0.0##}%), Busy: {6} ({7:0.0##}%)",
                totalStats.Successes,
                100.0 * totalStats.Successes / Config.NUM_REQUESTS,
                totalStats.Failures,
                100.0 * totalStats.Failures / Config.NUM_REQUESTS,
                totalStats.Late,
                100.0 * totalStats.Late / Config.NUM_REQUESTS,
                totalStats.Busy,
                100.0 * totalStats.Busy / Config.NUM_REQUESTS);
//#if DEBUG
//            Console.Read();            
//#endif
        }

        public void Uninitialize()
        {
            try
            {
                for (int i = 0; i < Config.NUM_WORKERS; i++)
                {
                    try
                    {
                        Workers[i].Uninitialize();
                    }
                    catch (RemotingException)
                    {
                        WriteProgress("Ignoring Workers[{0}].Uninitialize() RemotingException.", i);
                    }
                    catch (Exception exc2)
                    {
                        WriteProgress("Workers[{0}].Uninitialize() has throw an exception: {1}", i, exc2);
                    }
                }
            }
            catch (Exception exc)
            {
                WriteProgress("LoadTestDriverBase.Uninitialize() has throw an exception: {0}", exc);
            }
        }

        private bool ReportCallback(string workerName, int nSuccess, int nFailures, int nLate, int nBusy, int nPipelineSize, Latency aggregateLatency)
        {
            //LoadTestDriverBase.WriteProgress("Block Report: {0}, Pipeline: {1}", workerName, nPipelineSize);
            lock (typeof(LoadTestDriverBase))
            {
                nReports++;
                blockStats.Add(nSuccess, nFailures, nLate, nBusy, nPipelineSize);
                DateTime now = DateTime.UtcNow;
                bool endOfBlock = (nReports % Config.NUM_REPORTS_IN_BLOCK == 0);
                
                int numWarmupReports        = Config.NUM_WARMUP_BLOCKS * Config.NUM_REPORTS_IN_BLOCK;
                bool isWarmupReport         = nReports <= numWarmupReports;
                bool isLastWarmupReport     = nReports == numWarmupReports;
                int nonWarmupReportNumber = isWarmupReport ? 0 : nReports - numWarmupReports;

                if (endOfBlock)
                {
                    blockStats.EndBlock();
                    float cpuUsage = cpuCounter.NextValue();
                    blockStats.RecordCPU(cpuUsage);
                    
                    if (isLastWarmupReport)
                    {
                        // stat counting reports from scratch after warm-up.
                        superBlockStats.StartBlock();
                        totalStats.StartBlock();
                    }
                    else
                    {
                        totalStats.Add(blockStats);
                        superBlockStats.Add(blockStats);
                    }

                    latestTPS = blockStats.GetBlockTPS();
                    totalTPS = totalStats.GetTPSTillNow();

                    WriteProgress("{0}, {1}Current TPS: {2:0.0}, PipeSize: {3}, Successes: {4}, Failures: {5}, Late: {6}, Busy: {7}, Block Time: {8}, CPU now: {9:0.0}, [{10}]",
                        isWarmupReport ? nReports * Config.NUM_REQUESTS_IN_REPORT : nonWarmupReportNumber * Config.NUM_REQUESTS_IN_REPORT,
                        isWarmupReport ? "Warm-up Block " : "",
                        //1000.0 * ((float)blockStats.Successes / blockStats.TotalExecutionTime.TotalMilliseconds),
                        latestTPS,
                        blockStats.PipelineSize / Config.NUM_REPORTS_IN_BLOCK * Config.NUM_WORKERS,
                        blockStats.Successes,
                        blockStats.Failures,
                        blockStats.Late,
                        blockStats.Busy,
                        blockStats.TotalExecutionTimeString,
                        blockStats.GetAverageCPU(), 
                        TraceLogger.PrintDate(now));

                    // reset the current block.
                    blockStats = new BlockStats();
                    blockStats.StartBlock();

                    // every 10 block reports, print total till now.
                    int numReportsInSuperBlock = Config.NUM_REPORTS_IN_BLOCK * 10;
                    bool endOfSuperBlock = !isWarmupReport && (nonWarmupReportNumber % numReportsInSuperBlock == 0);

                    if (endOfSuperBlock)
                    {
                        superBlockStats.EndBlock();

                        WriteProgress("Superblock: Num Requests {0}, TPS: {1:0.0}, PipeSize: {2}, Successes : {3}, Failures : {4}, Late: {5}, Busy: {6}, Super Block Time: {7}, Average superblock CPU: {8:0.0}, [{9}]",
                            nonWarmupReportNumber * Config.NUM_REQUESTS_IN_REPORT,
                            //1000.0 * ((float)superBlockStats.Successes / superBlockStats.TotalExecutionTime.TotalMilliseconds),
                            superBlockStats.GetBlockTPS(),
                            superBlockStats.PipelineSize / numReportsInSuperBlock * Config.NUM_WORKERS,
                            superBlockStats.Successes,
                            superBlockStats.Failures,
                            superBlockStats.Late,
                            superBlockStats.Busy,
                            superBlockStats.TotalExecutionTimeString,
                            superBlockStats.GetAverageCPU(),
                            TraceLogger.PrintDate(now));

                        superBlockStats = new BlockStats();
                        superBlockStats.StartBlock();
                        totalTPS = totalStats.GetTPSTillNow();

                        //TimeSpan timeTillNow = totalStats.ExecutionTimeTillNow;
                        WriteProgress("*Total till now from start: TPS: {0:0.0}, Successes : {1}, Failures : {2}, Late: {3}, Busy: {4}, Time till now: {5}, Average CPU till now: {6:0.0}, [{7}]",
                             //1000.0 * ((float)totalStats.Successes / timeTillNow.TotalMilliseconds),
                             totalTPS,
                             totalStats.Successes,
                             totalStats.Failures,
                             totalStats.Late,
                             totalStats.Busy,
                             BlockStats.TimeSpanToString(totalStats.ExecutionTimeTillNow),
                             totalStats.GetAverageCPU(),
                             TraceLogger.PrintDate(now));
                    }
                }
                return endOfBlock;
            }
        }

        public static void WriteProgress(string format, params object[] args)
        {
            // DO NOT CHANGE THIS FORMAT. It is used by the parser of the LoadTest.
            string str = String.Format(format, args);
            Console.WriteLine(str);
            // logWriter is null from other app domains.
            if (logWriter != null)
            {
                logWriter.WriteToLog(str, Logger.Severity.Info);
                logWriter.Flush();
            }
        }
    }
}
