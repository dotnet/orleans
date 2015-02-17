using System;
using System.Threading;
using Orleans;
using Orleans.Runtime;

using LoadTestBase;

namespace LoadTest.Streaming
{
    public class SMSPubSubSubcribeBenchmarkDriver
    {
        // application configuration values.
        private static int NUM_STREAMS = 1000;
        private static int NUM_CONSUMERS_PER_STREAM = 1;
        private static int NUM_PRODUCERS_PER_STREAM = 1;
        private static bool SHARE_STREAMS = false;
        private static bool DoUnsubscribe = false;
        // this is how large the barrier should be that permits all clients to start simultaneously. it should be set to the number of workers in the cluster. currently, only one worker per client is supported, so this will be the number of clients in the cluster.
        private static int START_BARRIER_SIZE = 1;
        private static double Verbosity = 0;

        private const string testName = "SMS_Subscribe_Benchmark";

        public static void Main(string[] args)
        {
            LoadTestDriverBase driver;
            LoadTestBaseConfig config;

            try
            {
                driver = new LoadTestDriverBase(testName);

                // either use hard coded config for base configuration or pass cmd line args
                config = LoadTestBaseConfig.GetDefaultConfig();
                config.NUM_REQUESTS = 1 * 1000 * 1000;
                config.NUM_WORKERS = 1;
                config.NUM_THREADS_PER_WORKER = 8;
                config.PIPELINE_SIZE = 20 * 1000;

                config.NUM_REQUESTS_IN_REPORT = 50;
                config.NUM_REPORTS_IN_BLOCK = 10;
                config.NUM_WARMUP_BLOCKS = 1;

                config = driver.InitConfig(args, config);

                // either use hard coded config values for application configuration or pass cmd line args
                bool ok = ParseApplicationArguments(args);
                if (!ok) return;

                LoadTestDriverBase.WriteProgress(testName + ": Start. now={0}", TraceLogger.PrintDate(DateTime.UtcNow));
                LoadTestDriverBase.WriteProgress(testName + ": ParseArgs. {0}", PrintParams());

                ok = driver.Initialze(typeof(SMSPubSubSubcribeBenchmarkWorker));
                if (!ok)
                {
                    return;
                }
                
                for (int i = 0; i < driver.Workers.Length; i++)
                {
                    var worker = driver.Workers[i] as SMSPubSubSubcribeBenchmarkWorker;

                    worker.ApplicationInitialize(
                        NUM_STREAMS,
                        NUM_CONSUMERS_PER_STREAM,
                        NUM_PRODUCERS_PER_STREAM,
                        DoUnsubscribe,
                        START_BARRIER_SIZE,
                        SHARE_STREAMS,
                        verbosity: Verbosity);

                    Thread.Sleep(10);
                }
                LoadTestDriverBase.WriteProgress("Done ApplicationInitialize by all workers");
                LoadTestDriverBase.WriteProgress("\n\n*********************************************************\n");
            }
            catch (Exception e)
            {
                LoadTestDriverBase.WriteProgress("Driver.Main: FAIL {0}", e.ToString());
                LoadTestDriverBase.WriteProgress("\n\n*********************************************************\n");
                throw;
            }

            // start the actual load test.
            driver.Run();

            driver.Uninitialize();

            driver.WriteTestResults();

            LoadTestDriverBase.WriteProgress("Done the whole test.\nApp params: {0},\nConfig params: {1}", 
                PrintParams(), config.ToString());
        }

        private static string PrintParams()
        {
            return String.Format(
                "NUM_STREAMS = {0}, CONSUMERS_PER_STREAM = {1}, PRODUCERS_PER_STREAM = {2}, DoUnsubscribe={3} SHARE_STREAMS={4}, START_BARRIER_SIZE={5}", 
                NUM_STREAMS, NUM_CONSUMERS_PER_STREAM, NUM_PRODUCERS_PER_STREAM, DoUnsubscribe, SHARE_STREAMS, START_BARRIER_SIZE);
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
                
                if (a == "-streamCount") // number of streams
                {
                    NUM_STREAMS = int.Parse(args[++i]);
                }
                else if (a == "-consumersPerStream") // number of consumers per stream
                {
                    NUM_CONSUMERS_PER_STREAM = int.Parse(args[++i]);
                }
                else if (a == "-publishersPerStream") // number of publishers per stream
                {
                    NUM_PRODUCERS_PER_STREAM = int.Parse(args[++i]);
                }
                else if (a == "-shareStreams") // number of consumers per stream
                {
                    SHARE_STREAMS = Boolean.Parse(args[++i]);
                }
                else if (a == "-unsub") // Whether to do both Subscribe and Unsubscribe in each test iteration.
                {
                    DoUnsubscribe = Boolean.Parse(args[++i]);
                }
                else if (a == "-startBarrierSize") // size of the start barrier.
                {
                    START_BARRIER_SIZE = int.Parse(args[++i]);
                }
            }
            return true;
        }
    }
}
