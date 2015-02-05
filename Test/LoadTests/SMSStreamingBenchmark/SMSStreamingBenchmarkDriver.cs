using System;
using System.Threading;
using Orleans;
using Orleans.Runtime;

using LoadTestBase;

namespace SMSStreamingBenchmark
{
    public class SMSStreamingBenchmarkDriver
    {
        // application configuration values.
        private static int NUM_STREAMS = 1000;
        private static int NUM_CONSUMERS_PER_STREAM = 1;
        private static bool SHARE_STREAMS = false;

        public static void Main(string[] args)
        {
            LoadTestDriverBase driver;
            LoadTestBaseConfig config;

            try
            {
                driver = new LoadTestDriverBase("SMSStreamingBenchmark");

                // either use hard coded config for base configuration or pass cmd line args
                config = LoadTestBaseConfig.GetDefaultConfig();
                config.NUM_REQUESTS = 1 * 1000 * 1000;
                config.NUM_WORKERS = 1;
                config.NUM_THREADS_PER_WORKER = 8;
                config.PIPELINE_SIZE = 20 * 1000;
                config.NUM_WARMUP_BLOCKS = 1;

                config = driver.InitConfig(args, config);

                // either use hard coded config values for application configuration or pass cmd line args
                ParseApplicationArguments(args);

                LoadTestDriverBase.WriteProgress("[mlr] Build #8");
                LoadTestDriverBase.WriteProgress("SMSStreamingBenchmarkDriver: start. now={0}", TraceLogger.PrintDate(DateTime.UtcNow));
                LoadTestDriverBase.WriteProgress("SMSStreamingBenchmarkDriver: parseargs. {0}", PrintParams());

                bool ok = driver.Initialze(typeof(SMSStreamingBenchmarkWorker));
                if (!ok)
                {
                    return;
                }
                
                for (int i = 0; i < driver.Workers.Length; i++)
                {
                    ((SMSStreamingBenchmarkWorker)driver.Workers[i]).ApplicationInitialize(
                        NUM_STREAMS,
                        NUM_CONSUMERS_PER_STREAM,
                        SHARE_STREAMS,
                        SMSStreamingBenchmarkWorker.DefaultVerbosity);

                    Thread.Sleep(10);
                }
                LoadTestDriverBase.WriteProgress("Done ApplicationInitialize by all workers");
                LoadTestDriverBase.WriteProgress("\n\n*********************************************************\n");
            }
            catch (Exception e)
            {
                LoadTestDriverBase.WriteProgress("SMSStreamingBenchmarkDriver.Main: FAIL {0}", e.ToString());
                LoadTestDriverBase.WriteProgress("\n\n*********************************************************\n");
                throw;
            }


            // start the actual load test.
            driver.Run();

            driver.Uninitialize();

            driver.WriteTestResults();

            LoadTestDriverBase.WriteProgress("Done the whole test.\nApp params: {0},\nConfig params: {1}", PrintParams(), config.ToString());
        }

        private static string PrintParams()
        {
            return String.Format("NUM_STREAMS = {0}, NUM_CONSUMERS_PER_STREAM = {1}", NUM_STREAMS, NUM_CONSUMERS_PER_STREAM);
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
                else if (a == "-streamCount") // number of streams
                {
                    NUM_STREAMS = int.Parse(args[++i]);
                }
                else if (a == "-consumersPerStream") // number of consumers per stream
                {
                    NUM_CONSUMERS_PER_STREAM = int.Parse(args[++i]);
                }
                else if (a == "-shareStreams") // number of consumers per stream
                {
                    SHARE_STREAMS = Boolean.Parse(args[++i]);
                }
            }
            return true;
        }
    }
}