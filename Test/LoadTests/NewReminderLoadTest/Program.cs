using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using CommandLine.Text;
using Orleans;
using Orleans.Runtime;
using LoadTestBase;
using LoadTestGrainInterfaces;

namespace NewReminderLoadTest
{
   public class Program
    {
        public class Options
        {
            [Option('c', "config", DefaultValue = "ClientConfiguration.xml", HelpText = "path name of client configuration file")]
            public string ClientConfigFile { get; set; }

            [Option('v', "verbose", DefaultValue = true, HelpText = "turn on verbose silo logging")]
            public bool Verbose { get; set; }

            [Option('m', "embed-silos", HelpText = "use a number of embedded silos")]
            public int EmbedSilos { get; set; }
            
            [Option('s', "silo-config", DefaultValue = "OrleansConfiguration.xml", HelpText = "path name of silo configuration file")]
            public string SiloConfigFile { get; set; }

            [Option('d', "deployment-id", HelpText = "specify the deployment id (defaults to none)")]
            public string DeploymentId { get; set; }

            [Option("grain-pool-size", DefaultValue = 10000, HelpText = "specify the size of the grain pool")]
            public int GrainPoolSize { get; set; }

            [Option("reminder-pool-size", DefaultValue = 100000, HelpText = "specify the size of the grain pool")]
            public int ReminderPoolSize { get; set; }

            [Option("no-unregister", DefaultValue = false, HelpText = "specify the size of the grain pool")]
            public bool NoUnregister { get; set; }

            [Option('n', DefaultValue = 1000000, HelpText = "specify the number of reminders to register")]
            public int NumReminders { get; set; }

            [Option("start-barrier-size", DefaultValue = 1, HelpText = "specify the size of the starting barrier (equal to the number of clients in the test)")]
            public int StartBarrierSize { get; set; }

            [Option("share-grain-pool", DefaultValue = true, HelpText = "specify whether the grain pool should be shared between clients.")]
            public bool ShareGrainPool { get; set; }

            // the following options are here because the deployment framework requires them.

            [Option('p', HelpText = "ignored")]
            public int PipelineSize { get; set; }

            [Option('w', HelpText = "ignored")]
            public int NumberOfWorkers { get; set; }

            [Option('t', HelpText = "ignored")]
            public int NumberOfThreads { get; set; }

            [Option("azure", HelpText = "ignored")]
            public bool UseAzureSiloTable { get; set; }

            [Option("testId", HelpText = "ignored")]
            public bool TestId { get; set; }

            [HelpOption]
            public string GetUsage()
            {
                return HelpText.AutoBuild(this);
            }
        }

        private static OrleansHostWrapper _hostWrapper;
        private static LoadTestDriverBase _driver;
        private static Options _options;

        public static int Main(string[] args)
        {
            try
            {
                string[] legacyArgs = args;

                // The command line parser doesn't recognize single-dash long options for longer than 1 char options, so add an extra "-".
                args = args.Select(s => s.StartsWith("-") && s.Length > 2 && !s.Substring(1).StartsWith("-") ? "-" + s : s).ToArray();

                LogAlways(String.Format("Started with arguments: {0}.", LoadTestGrainInterfaces.Utils.EnumerableToString(args)));

                if (ParseArguments(args, out _options))
                {
                    IEnumerable<AppDomain> hostDomains = null;
                    if (_options.EmbedSilos > 0)
                    {
                        hostDomains = StartEmbeddedSilos(_options.EmbedSilos, args);  
                    }
                    _driver = new LoadTestDriverBase(Assembly.GetExecutingAssembly().GetName().Name);

                    GrainClient.Initialize(_options.ClientConfigFile);
                    LogIfVerbose("Client is initialized.\n");

                    // begin test-specific code
                    // either use hard coded config for base configuration or pass cmd line args
                    LoadTestBaseConfig config = LoadTestBaseConfig.GetDefaultConfig();
                    config.PIPELINE_SIZE = 2500;
                    config.NUM_WORKERS = 1;
                    // unless explicitly specified, use enough warmup blocks to ensure that all grains in the grain pool are activated.
                    config.NUM_REQUESTS_IN_REPORT = 1000;
                    config.NUM_REPORTS_IN_BLOCK = 10;
                    int numRegisterOnlyBlocks = _options.ReminderPoolSize / (config.NUM_REQUESTS_IN_REPORT * config.NUM_REPORTS_IN_BLOCK);
                    // tests show that it takes three times the pool size requests to eliminate initial instability (timeouts).
                    config.NUM_WARMUP_BLOCKS = numRegisterOnlyBlocks * 3;
                    config = _driver.InitConfig(legacyArgs, config);
                    LogAlways(string.Format("LoadTestDriverBase Parameterization: {0}", config));

                    bool ok = _driver.Initialze(typeof(Worker));
                    if(!ok)
                    {
                        return 1;
                    }
                    LoadTestDriverBase.WriteProgress("Worker Initialize() complete.");

                    for (int i = 0; i < _driver.Workers.Length; i++)
                    {
                        ((Worker)_driver.Workers[i]).ApplicationInitialize(_options.GrainPoolSize, _options.ReminderPoolSize, _options.StartBarrierSize, _options.ShareGrainPool);

                        Thread.Sleep(10);
                    }
            
                    LoadTestDriverBase.WriteProgress("Worker ApplicationInitialize() complete.");
                    _driver.Run();
                    _driver.WriteTestResults();            
                    // end test-specific code

                    // signal the framework to that the test is finished.
                    LogAlways("Done the whole test.\n");

                    if (hostDomains != null)
                    {
                        StopEmbeddedSilos(hostDomains);
                    }

                    return 0;
                }
                else
                {
                    _options = new Options();
                    LogAlways(String.Format("Failed to parse arguments: {0}.", LoadTestGrainInterfaces.Utils.EnumerableToString(args)));
                    LogAlways(String.Format("Usage:\n{0}.", _options.GetUsage()));
                    return 1;
                }
            }
            catch (Exception e1)
            {
                try
                {
                    string outPath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                    using (StreamWriter outFile = new StreamWriter(outPath + @"\unhandled_exception.log"))
                    {
                        outFile.Write(e1.ToString());
                    }
                    return -1;
                }
                catch (Exception e2)
                {
                    
                    throw new AggregateException(new[]{e1, e2});
                }
            }
        }

        private static bool ParseArguments(string[] args, out Options result)
        {
            Options options = new Options();
            if (Parser.Default.ParseArguments(args, options))
            {
                result = options;
                return true;
            }
            else
            {
                result = null;
                return false;
            }
        }

        private static IEnumerable<AppDomain> StartEmbeddedSilos(int siloCount, string[] args)
        {
            if (siloCount < 1)
            {
                throw new ArgumentOutOfRangeException("siloCount", siloCount, "Silo count is less than 1");
            }
            ParseArguments(args, out _options);
            List<AppDomain> result = new List<AppDomain>();
            for (int i = 0; i < siloCount; ++i)
            {
                result.Add(StartEmbeddedSilo(i, args));
            }
            return result;
        }

        private static AppDomain StartEmbeddedSilo(int siloId, string[] args)
        {
            // The Orleans silo environment is initialized in its own app domain in order to more
            // closely emulate the distributed situation, when the client and the server cannot
            // pass data via shared memory.
            string siloName = String.Format("Silo{0:d2}", siloId);

            LogIfVerbose(String.Format("Starting embedded silo \"{0}\"...", siloName));

            AppDomain appDomain = AppDomain.CreateDomain(siloName, null, new AppDomainSetup
            {
                AppDomainInitializer = StartSiloHost,
                AppDomainInitializerArguments = args,
            });
            return appDomain;
        }

        private static void StartSiloHost(string[] args)
        {
            ParseArguments(args, out _options);
            string siloName = AppDomain.CurrentDomain.FriendlyName;
            _hostWrapper = new OrleansHostWrapper(_options.SiloConfigFile, siloName, _options.DeploymentId, _options.Verbose);

            if (!_hostWrapper.Run())
            {
                Console.Error.WriteLine("Failed to initialize Orleans silo");
            }

            LogIfVerbose(String.Format("Started embedded silo \"{0}\"...", siloName));
        }

        private static void StopEmbeddedSilos(IEnumerable<AppDomain> hostDomains)
        {
            foreach (var domain in hostDomains)
            {
                domain.DoCallBack(StopEmbeddedSilo);
            }      
        }

        private static void StopEmbeddedSilo()
        {
            if (_hostWrapper != null)
            {
                _hostWrapper.Dispose();
            }
        }

        private static void LogAlways(string msg)
        {
            Trace.WriteLine(msg);
            LoadTestDriverBase.WriteProgress(msg);
        }
        private static void LogIfVerbose(string msg)
        {
            Trace.WriteLine(msg);
            if (_options.Verbose)
            {
                LoadTestDriverBase.WriteProgress(msg);
            }
        }
    }
}
