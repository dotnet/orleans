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

namespace ReminderLoadTest
{
    public class ReminderLoadTestDriver
    {
        public static void Main(string[] args)
        {
            var driver = new LoadTestDriverBase("ReminderLoadTest");

            // either use hard coded config for base configuration or pass cmd line args
            LoadTestBaseConfig config = LoadTestBaseConfig.GetDefaultConfig();
            config.NUM_REQUESTS = 2;
            config.NUM_WORKERS = 1;
            config.NUM_THREADS_PER_WORKER = 1;
            config.PIPELINE_SIZE = 20 * 1000;
            config.GW_PORT = 30000;
            config.GW_INSTANCE_INDEX = -1;
            config.USE_AZURE_SILO_TABLE = false;
            config.NUM_WARMUP_BLOCKS = 1;

            LoadTestDriverBase.WriteProgress("Starting ReminderLoadTest at: [{0}]", TraceLogger.PrintDate(DateTime.UtcNow));
            LoadTestDriverBase.WriteProgress("LoadTestDriverBase Initialize");

            config = driver.InitConfig(args, config);
            bool ok = driver.Initialze(typeof(ReminderLoadTestWorker));
            if (!ok)
            {
                return;
            }
            
            for (int i = 0; i < driver.Workers.Length; i++)
            {
                ((ReminderLoadTestWorker) driver.Workers[i]).ApplicationInitialize();
            }

            LoadTestDriverBase.WriteProgress("Done ApplicationInitialize by all workers");
            LoadTestDriverBase.WriteProgress("\n\n*********************************************************\n");

            // start the actual load test.
            driver.Run();

            driver.Uninitialize();

            driver.WriteTestResults();

            LoadTestDriverBase.WriteProgress("Done the whole test.\nConfig params: {1}", config.ToString());
        }
    }
}
