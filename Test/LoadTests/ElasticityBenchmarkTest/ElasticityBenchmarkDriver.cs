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
    public class ElasticBenchmarkDriver
    {
        // application configuration values.
        private static int NUM_GRAINS = 1000;
        private static BenchmarkFunctionType FUNCTION_TYPE = BenchmarkFunctionType.ReuseGrains;
        private static bool WARM_UP_GRAINS = false;
        public static string EXCEL_NAME = "UNSET.xlsx";

        public static void Main(string[] args)
        {
            LoadTestDriverBase driver = new LoadTestDriverBase("GrainBenchmark");

            // either use hard coded config for base configuration or pass cmd line args
            LoadTestBaseConfig config = LoadTestBaseConfig.GetDefaultConfig();
            
            config.NUM_REQUESTS = 7 * 1000 * 1000;
            config.NUM_WORKERS = 1;
            config.NUM_THREADS_PER_WORKER = 8;
            config.PIPELINE_SIZE = 20 * 1000;
            config.NUM_WARMUP_BLOCKS = 1;

            config = driver.InitConfig(args, config);

            // either use hard coded config values for application configuration or pass cmd line args
            ParseApplicationArguments(args);

            LoadTestDriverBase.WriteProgress("Starting ElasticBenchmarkDriver at: [{0}]", TraceLogger.PrintDate(DateTime.UtcNow));
            LoadTestDriverBase.WriteProgress("ElasticBenchmarkDriver Initialize with App params: {0}", PrintParams());

            bool ok = driver.Initialze(typeof(ElasticityBenchmarkWorker));
            if (!ok)
            {
                return;
            }
            
            for (int i = 0; i < driver.Workers.Length; i++)
            {
                ((ElasticityBenchmarkWorker)driver.Workers[i]).ApplicationInitialize(NUM_GRAINS, FUNCTION_TYPE, WARM_UP_GRAINS);
                Thread.Sleep(10);
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
            return String.Format("NUM_GRAINS = {0}, FUNCTION_TYPE = {1} EXCEL_NAME = {2} WARM_UP_GRAINS = {3}",
                NUM_GRAINS, 
                FUNCTION_TYPE,
                EXCEL_NAME,
                WARM_UP_GRAINS);
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
                else if (a == "-grains" || a == "-g") // number of grains
                {
                    NUM_GRAINS = int.Parse(args[++i]);
                }
                else if (a == "-functionType") // type of a grain function to use
                {
                    FUNCTION_TYPE = (BenchmarkFunctionType)Enum.Parse(typeof(BenchmarkFunctionType), args[++i], true);
                }
                else if (a == "-warmUp")
                {
                    WARM_UP_GRAINS = true;
                }
                else if (a == "-excelName")
                {
                    EXCEL_NAME = args[++i];
                }
            }
            return true;
        }

    }
}
