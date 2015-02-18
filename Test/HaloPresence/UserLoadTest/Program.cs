using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using LoadTestBase;

namespace PresenceConsoleTest
{
    class MyProgram
    {
        private static readonly List<IPEndPoint> gatewayList = new List<IPEndPoint>();

        private const int NUM_REPORTS_IN_BLOCK = 20;
        private const int NUM_AZURE_SILOS = 25;  // to do should this be const
        private const int REPORT_BLOCK_SIZE = 10000;

        // These are command line arguments
        private static IPEndPoint[] gateways;
        private static int gatewayPort = 30000;
        private static long _numRequests = 100 * 1000 * 1000;
        private static int NUM_USERS = 500 * 1000;
        private static int NUM_WORKERS = 5;
        private static int NUM_INITIAL_BLOCKS = 5;
        private static int NUM_MAX_BLOCKS = 50;
        private static bool RandomBuckets;
        private static int PIPELINE_SIZE = 500;
        private static bool warmup = false;
        private static bool HeartbeatFormatTU1 = false;
        private static int RAN_TIMER_INTERVAL = 1;
        private static bool UseAzureSiloTable;

        private static int nReports;
        private static DateTime previousReport;
        private static int totalFailures;
        private static int totalLate;
        private static int pipelineSize;
        private static int instanceIndex = -1;
        private static Latency totalAggregateLatency;

        private static IPEndPoint GetGatewayEndpoint(string host)
        {
            IPAddress[] addresses;
            try
            {
                addresses = Dns.GetHostAddresses(host);
            }
            catch (Exception ex)
            {
                throw new ApplicationException(string.Format("Socket exception resolving host '{0}'", host), ex);                
            }
            foreach (IPAddress address in addresses)
                if (address.AddressFamily == AddressFamily.InterNetwork)
                    return new IPEndPoint(address, 30000);

            throw new ApplicationException(string.Format("Cannot resolve {0} to an IPv4 address", host));
        }

        private static string CommandLineHelp()
        {
            var sb = new StringBuilder();

            sb.AppendLine("USAGE:");
            sb.AppendLine("    PresenceConsoleTest [-w | -n (# of requests) | -users (# of users)");
            sb.AppendLine("                     | -threads (# of worker threads)");
            sb.AppendLine("                     | -port (gateway default port)");
            sb.AppendLine("                     | -instanceIndex (index of the instance)] (GatewayEndpoint)");
            sb.AppendLine("where");
            sb.AppendLine("    GatewayEndpoint                List of IPs to use for gateways.  This list ");
            sb.AppendLine("                                   is space seperated.  i.e. IP#1 IP#2 ...");
            sb.AppendLine("    Options:");
            sb.AppendLine("       -w                          Precreates the user and session grains.");
            sb.AppendLine("       -n                          Number of requests.  Defaults to 100000000.");
            sb.AppendLine("                                   If set to 0 the test will run indefintely.");
            sb.AppendLine("       -users or -u                Number of users.  Defaults to 500000.");
            sb.AppendLine("       -threads or -t              Number of work threads.  Defaults to 5.");
            sb.AppendLine("       -port                       Gateway's port number. Defaults to 30000.");
            sb.AppendLine("       -instanceIndex              The index number of the Gateway to use ");
            sb.AppendLine("                                   from ClientConfiguration.xml.");
            sb.AppendLine("       -initialBlocks or -ib       The initial number of user blocks of 1000 per thread.");
            sb.AppendLine("                                   The default is 5, this is good for 5 threads and 5 gateways.");
            sb.AppendLine("       -maxBlocks or -mb           The max number of blocks.  This is so that");
            sb.AppendLine("                                   the system doesn't become overwhelmed.");
            sb.AppendLine("                                   If the value is set to something less then");
            sb.AppendLine("                                   1 there is no set maximum.");
            sb.AppendLine("       -random or -r               Makes the buckets that are added and removed");
            sb.AppendLine("                                   random.");
            sb.AppendLine("       -tu1                        Send heartbeats inTU1 format.");
            sb.AppendLine("       -timer                      The interval (in minutes) for recycling users buckets. ");
            sb.AppendLine("       -azure                        Use gateway list from Azure table OrleansSiloInstances rather than config file");
            sb.AppendLine();
            sb.AppendLine("Examples:");
            sb.AppendLine("     > PresenceConsoleTest                       ... Show information");
            sb.AppendLine("     > PresenceConsoleTest MachineName           ... Runs with all defaults");
            sb.AppendLine("                                                     with one gateway.");
            sb.AppendLine("     > PresenceConsoleTest -w MachineName        ... Runs with warm-up.");
            sb.AppendLine("     > PresenceConsoleTest -w -n 1000 -u 50 -t 2 MachineName");
            sb.AppendLine("     > PresenceConsoleTest -ib 20 -mb 100 MachineName");
            return sb.ToString();
        }

        private static bool ParseArguments(string[] args)
        {
            if (args.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i].Trim().ToLower();

                if (a == "-?" || a == "-help" || a == "/?" || a == "/help")
                {
                    return false;
                }
                else if (a == "-w")
                {
                    warmup = true;
                }
                else if (a == "-n")
                {
                    _numRequests = long.Parse(args[++i]);
                }
                else if (a == "-users" || a == "-u")
                {
                    NUM_USERS = int.Parse(args[++i]);
                }
                else if (a == "-pipe" || a == "-pipeline" || a == "-p")
                {
                    PIPELINE_SIZE = int.Parse(args[++i]);
                }
                else if (a == "-threads" || a == "-t")
                {
                    NUM_WORKERS = int.Parse(args[++i]);
                }
                else if (a == "-port")
                {
                    gatewayPort = int.Parse(args[++i]);
                }
                else if (a == "-instanceIndex")
                {
                    instanceIndex = int.Parse(args[++i]);
                }
                else if (a == "-initialBlocks" || a == "-ib")
                {
                    NUM_INITIAL_BLOCKS = int.Parse(args[++i]);
                }
                else if (a == "-maxBlocks" || a == "-mb")
                {
                    NUM_MAX_BLOCKS = int.Parse(args[++i]);
                }
                else if (a == "-random" || a == "-r")
                {
                    RandomBuckets = true;
                }
                else if (a == "-timer")
                {
                    RAN_TIMER_INTERVAL = int.Parse(args[++i]);
                }
                else if (a == "-tu1")
                {
                    HeartbeatFormatTU1 = true;
                }
                else if (a == "-azure")
                {
                    UseAzureSiloTable = true;
                }
                else if (a == "-?" || a == "-help") { }
                else if (a == "-gw")
                {
                    gatewayList.Add(GetGatewayEndpoint(a));
                }
            }

            return true;
        }

        private static void Main(string[] args)
        {
            try
            {

                bool ok = ParseArguments(args);

                if (!ok)
                {
                    Console.Write(CommandLineHelp());
                    return;
                }

                ThreadPool.SetMinThreads(500, 500);

                Orleans.GrainClient.Initialize();
            
                gateways = gatewayList.ToArray();
                string workerClassName = typeof(UserWorker).FullName;

                if (NUM_WORKERS <= 0)
                {
                    NUM_WORKERS = 1;
                }
                var appDomains = new AppDomain[NUM_WORKERS];
                var workers = new UserWorker[NUM_WORKERS];
                var callback = new Callback(ReportCallback);
                var promises = new Task<List<Exception>>[NUM_WORKERS];

                for (int i = 0; i < NUM_WORKERS; i++)
                {
                    appDomains[i] = AppDomain.CreateDomain("Worker" + i);
                    workers[i] = (UserWorker)appDomains[i].CreateInstanceAndUnwrap(Assembly.GetExecutingAssembly().FullName, workerClassName);
                    workers[i].HeartbeatFormatTU1 = HeartbeatFormatTU1;

                    IPEndPoint gw = null;
                    if (instanceIndex >= 0)
                    {
                        // Use specified gateway from command line
                        gw = gateways[(instanceIndex + i) % gateways.Length];
                    }
                    else if (gateways.Length != 0)
                    {
                        // Use specified gateway from command line
                        gw = gateways[i % gateways.Length];
                    }

                    workers[i].Initialize(
                        NUM_USERS, 
                        _numRequests / NUM_WORKERS, 
                        REPORT_BLOCK_SIZE, 
                        PIPELINE_SIZE, 
                        callback, 
                        gw, 
                        (instanceIndex * NUM_WORKERS + i) % NUM_AZURE_SILOS,
                        UseAzureSiloTable,
                        warmup && i == 0,
                        NUM_INITIAL_BLOCKS, 
                        NUM_MAX_BLOCKS, 
                        RAN_TIMER_INTERVAL,
                        RandomBuckets);

                    Thread.Sleep(10);
                }

                var start = DateTime.UtcNow;
                previousReport = DateTime.UtcNow;

                Orleans.GrainClient.Logger.Info("{2} Starting sending {0} heartbeats for {1} sessions...", _numRequests, NUM_USERS, DateTime.Now);

                for (int i = 0; i < NUM_WORKERS; i++)
                {
                    int index = i;
                    promises[i] = Task<List<Exception>>.Factory.StartNew(() => workers[index].Run());
                    Thread.Sleep(1 * 1000);
                }

                Task.WhenAll(promises).Wait();

                var end = DateTime.UtcNow;
                Orleans.GrainClient.Logger.Info(string.Format("Test completed successfully in {0} milliseconds", (end - start).TotalMilliseconds));
                Orleans.GrainClient.Logger.Info(string.Format("Average TPS : {0}", _numRequests / (end - start).TotalSeconds));
                Orleans.GrainClient.Logger.Info(totalAggregateLatency.ToString());      
            }                

            catch(Exception e)
            {
                List<string> text = new List<string>();
                    text.Add(e.Message);
                    text.Add("");
                    text.Add(e.StackTrace);
                System.IO.File.WriteAllLines(@"Errors.txt", text.ToArray());
            }
        }

        private static bool ReportCallback(string workerName, int nFailures, int nPipelineSize, int nLate, int nBusy, Latency aggregateLatency)
        {
            lock (typeof(MyProgram))
            {
                nReports++;

                totalFailures += nFailures;
                pipelineSize += nPipelineSize;
                totalLate += nLate;
                totalAggregateLatency = aggregateLatency;
                if (nReports % NUM_REPORTS_IN_BLOCK == 0)
                {
                    DateTime now = DateTime.UtcNow;
                    var sb = new StringBuilder();
                    sb.Append(string.Format("{0}, Current TPS: {1}, Pipeline size: {2}, Failures: {3}, Late: {4}, ", 
                        nReports * REPORT_BLOCK_SIZE, 
                        (float)REPORT_BLOCK_SIZE * NUM_REPORTS_IN_BLOCK / (now - previousReport).TotalSeconds, 
                        pipelineSize / NUM_REPORTS_IN_BLOCK * NUM_WORKERS, 
                        nFailures, 
                        nLate));
                    sb.Append(aggregateLatency.ToString());
                    Orleans.GrainClient.Logger.Info(sb.ToString());
                    previousReport = now;
                    pipelineSize = 0;
                    totalFailures = 0;
                    totalLate = 0;
                }
            }

            return true;
        }
    }
}
