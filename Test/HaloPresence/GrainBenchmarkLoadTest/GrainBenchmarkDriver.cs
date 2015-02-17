using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Orleans;
using Orleans.Runtime;
using LoadTestBase;
using LoadTestGrainInterfaces;

namespace GrainBenchmarkLoadTest
{
    // GrainBenchmarkDriver is an example application for writing high throughput client that can be used
    // to stress Orleans Silo under high load.
    // It uses the LoadTestDriverBase class to create a number of OrleansClients in separate AppDomains.
    // Each client worker (implemented in a class deriving from WorkerBase) will be running in a separate thread issuing grain calls to Orleans grains.
    // Throughout results are reported by each worker back to LoadTestDriverBase, aggregated and displayed.
    // The actual client application should define 2 functions inside a class that derives WorkerBase: 
    //  1) ApplicationInitialize which can be used to do some application specific initialization
    //  2) IssueRequest which issues one grain call.
    public class GrainBenchmarkDriver
    {
        // application configuration values.
        private static int NUM_GRAINS = 1000;
        private static int DATA_SIZE = 100;
        private static TimeSpan REQUEST_LATENCY = TimeSpan.FromMilliseconds(1000);

        // Changing this requires change to DeploymentManager!
        // This should be the absolute path locally at the machine running the load test, the DeploymentManager will push the file out to clients
        internal static string INPUT_GRAPH_FILE = null;

        private static BenchmarkGrainType GRAIN_TYPE = BenchmarkGrainType.LocalReentrant;
        private static BenchmarkGrainType NEXT_GRAIN_TYPE = BenchmarkGrainType.LocalReentrant;
        private static BenchmarkFunctionType FUNCTION_TYPE = BenchmarkFunctionType.PingImmutable;   // one hop - PingImmutable, 2 hops - PingImmutableArray_TwoHop

        public static void Main(string[] args)
        {
            LoadTestDriverBase driver = new LoadTestDriverBase("GrainBenchmark");

            // either use hard coded config for base configuration or pass cmd line args
            LoadTestBaseConfig config = LoadTestBaseConfig.GetDefaultConfig();
            config.NUM_REQUESTS = 1 * 1000 * 1000;
            config.NUM_WORKERS = 1;
            config.NUM_THREADS_PER_WORKER = 8;
            config.PIPELINE_SIZE = 20 * 1000;
            config.NUM_WARMUP_BLOCKS = 1;

            config = driver.InitConfig(args, config);

            // either use hard coded config values for application configuration or pass cmd line args
            ParseApplicationArguments(args);

            //LoadTestDriverBase.WriteProgress("This is Client #{0} build by Soramichi", "StatTotalShouldWork2");
            LoadTestDriverBase.WriteProgress("Starting GrainBenchmarkDriver at: [{0}]", TraceLogger.PrintDate(DateTime.UtcNow));
            LoadTestDriverBase.WriteProgress("GrainBenchmarkDriver Initialize with App params: {0}", PrintParams());

            bool ok = driver.Initialze(typeof(GrainBenchmarkWorker));
            if (!ok)
            {
                return;
            }
            
            for (int i = 0; i < driver.Workers.Length; i++)
            {
                ((GrainBenchmarkWorker)driver.Workers[i]).ApplicationInitialize(
                    NUM_GRAINS,
                    DATA_SIZE,
                    REQUEST_LATENCY,
                    GRAIN_TYPE,
                    NEXT_GRAIN_TYPE,
                    FUNCTION_TYPE,
                    INPUT_GRAPH_FILE);

                Thread.Sleep(TimeSpan.FromMilliseconds(10));
            }

            LoadTestDriverBase.WriteProgress("Done ApplicationInitialize by all workers");
            LoadTestDriverBase.WriteProgress("\n\n*********************************************************\n");

            // start the actual load test.
            driver.Run();

            driver.Uninitialize();

            driver.WriteTestResults();

            LoadTestDriverBase.WriteProgress("Done the whole test.\nApp params: {0},\nConfig params: {1}", PrintParams(), config.ToString());
        }

        private static string PrintParams()
        {
            return String.Format("NUM_GRAINS = {0}, DATA_SIZE = {1}, GRAIN_TYPE = {2}, NEXT_GRAIN_TYPE = {3}, FUNCTION_TYPE = {4}, REQUEST_LATENCY = {5}ms, ",
                NUM_GRAINS, 
                DATA_SIZE, 
                GRAIN_TYPE, 
                NEXT_GRAIN_TYPE,
                FUNCTION_TYPE, 
                REQUEST_LATENCY.TotalMilliseconds);
        }

        private static bool ParseApplicationArguments(string[] args)
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
                else if (a == "-d") // data size
                {
                    DATA_SIZE = int.Parse(args[++i]);
                }
                else if (a == "-grains" || a == "-g") // number of grains
                {
                    NUM_GRAINS = int.Parse(args[++i]);
                }
                else if (a == "-latency" || a == "-l") // number of grains
                {
                    REQUEST_LATENCY = TimeSpan.FromMilliseconds(long.Parse(args[++i]));
                }
                else if (a == "-grainType") // type of a grain to use
                {
                    GRAIN_TYPE = (BenchmarkGrainType)Enum.Parse(typeof(BenchmarkGrainType), args[++i], true);
                }
                else if (a == "-functionType") // type of a grain function to use
                {
                    FUNCTION_TYPE = (BenchmarkFunctionType)Enum.Parse(typeof(BenchmarkFunctionType), args[++i], true);
                }
            }
            return true;
        }
    }
}
