using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Orleans;
using LoadTestBase;
using System.Threading.Tasks;

namespace HaloStatsLoadTest
{
    public class HaloStatsDriver
    {
        // application configuration values.
        private static int NUM_GRAINS = 100;

        public static void Main(string[] args)
        {
            LoadTestDriverBase driver = new LoadTestDriverBase("HaloStats");

            // either use hard coded config for base configuration or pass cmd line args
            LoadTestBaseConfig config = LoadTestBaseConfig.GetDefaultConfig();
            config.NUM_REQUESTS = 100 * 1000; // 1 * 1000 * 1000;
            config.NUM_WORKERS = 5;
            config.PIPELINE_SIZE = 1000;
            config.GW_PORT = 30000;
            config.GW_INSTANCE_INDEX = -1;
            config.USE_AZURE_SILO_TABLE = false;
            config.NUM_REQUESTS_IN_REPORT = 1000;
            config.NUM_REPORTS_IN_BLOCK = 10;
            config.NUM_WARMUP_BLOCKS = 0;

            LoadTestDriverBase.WriteProgress("HaloStatsDriver Initialize with app params NUM_GRAINS = {0}", NUM_GRAINS);

            bool ok = driver.Initialze(typeof(HaloStatsWorker), null, config);
            if (!ok)
            {
                return;
            }

            // either use hard coded config values for application configuration or pass cmd line args
            //ok = ParseApplicationArguments(args);
            //if (!ok)
            //{
            //    LoadTestDriverBase.WriteProgress(CommandLineHelp());
            //    return;
            //}

            LoadTestDriverBase.WriteProgress("Staring ApplicationInitialize by all workers");
            List<AsyncCompletion> inits = new List<AsyncCompletion>();
            for (int i = 0; i < driver.Workers.Length; i++)
            {
                int capture = i;
                AsyncCompletion ac = AsyncCompletion.StartNew(() =>
                {
                    ((HaloStatsWorker)driver.Workers[capture]).ApplicationInitialize(NUM_GRAINS);
                });
                inits.Add(ac);
            }
            AsyncCompletion.JoinAll(inits).Wait();

            LoadTestDriverBase.WriteProgress("Done ApplicationInitialize by all workers");
            LoadTestDriverBase.WriteProgress("\n\n*********************************************************\n");

            // start the actual load test.
            driver.Run();

            LoadTestDriverBase.WriteProgress("Done the whole test");
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
            }
            return true;
        }
    }
}
