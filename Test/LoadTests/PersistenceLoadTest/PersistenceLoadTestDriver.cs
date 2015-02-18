using System;
using System.Text;
using LoadTestBase;
using LoadTestGrainInterfaces;

using Orleans.Runtime;

namespace Orleans.Tests.Persistence
{
    public interface IPersistenceWorker
    {
        void ApplicationInitialize(string name, long nGrains, PartitionKeyType partKeyType);
        TimeSpan AggregateLatency { get; }
    }

    public enum PartitionKeyType
    {
        None = 0,
        DifferentPerThread = 1,
        SharedBetweenThreads = 2,
        UniquePerRequest = 3,
    }

    internal class PersistenceLoadTestDriver
    {
        private const string TestName = "PersistenceLoadTest";

        public static void Main(string[] args)
        {
            try
            {
                var driver = new LoadTestDriverBase(TestName);
                LoadTestBaseConfig config = null;

                // either use hard coded config for base configuration or pass cmd line args
                if (args == null || args.Length == 0)
                {
                    config = LoadTestBaseConfig.GetDefaultConfig();
                    config.NUM_REQUESTS = 100 * 1000; // 100 * 1000 * 1000;
                    config.NUM_WORKERS = 1;
                    config.NUM_THREADS_PER_WORKER = Environment.ProcessorCount;
                    config.PIPELINE_SIZE = 500; // 20*1000;
                    config.DirectClientTest = true;
                }

                LoadTestDriverBase.WriteProgress("Starting {0} at: [{1}]", TestName, TraceLogger.PrintDate(DateTime.UtcNow));

                config = driver.InitConfig(args, config);
                config.NUM_WARMUP_BLOCKS = 0;
                config.NUM_REQUESTS_IN_REPORT = 1000;
                config.NUM_REPORTS_IN_BLOCK = 1;
                Type workerType = null;
                if (config.DirectClientTest)
                {
                    workerType = typeof(AzureStorageDirectWorker);
                }
                else
                {
                    workerType = typeof(PersistenceGrainWorker);
                }
                bool ok = driver.Initialze(workerType);
                if (!ok)
                {
                    return;
                }

                long numGrains = 10000;
                if (numGrains > config.NUM_REQUESTS) numGrains = config.NUM_REQUESTS;
                PartitionKeyType partitionKeyType = PartitionKeyType.DifferentPerThread;

                LoadTestDriverBase.WriteProgress("Application.Initialize TestName = {0}, numGrains = {1}, partitionKeyType = {2}", TestName, numGrains, partitionKeyType);
                for (int i = 0; i < driver.Workers.Length; i++)
                {
                    ((IPersistenceWorker)driver.Workers[i]).ApplicationInitialize(TestName, numGrains, partitionKeyType);
                }

                LoadTestDriverBase.WriteProgress("Done ApplicationInitialize by all {0} worker(s)", driver.Workers.Length);
                LoadTestDriverBase.WriteProgress("\n\n*********************************************************\n");

                // start the actual load test.
                driver.Run();

                LoadTestDriverBase.WriteProgress("End of run phase");
                LoadTestDriverBase.WriteProgress("\n\n*********************************************************\n");

                //CleanupApplication();

                driver.Uninitialize();

                LoadTestDriverBase.WriteProgress("Results:");
                LoadTestDriverBase.WriteProgress("\n\n*********************************************************\n");

                driver.WriteTestResults();

                TimeSpan totalLatency = TimeSpan.Zero;
                for (int i = 0; i < driver.Workers.Length; i++)
                {
                    totalLatency += ((IPersistenceWorker)driver.Workers[i]).AggregateLatency;
                }
                TimeSpan averageLatency = Divide(totalLatency, config.NUM_REQUESTS);
                LoadTestDriverBase.WriteProgress(String.Format("\n\nAverage Latency is {0}\n", averageLatency));
                
                LoadTestDriverBase.WriteProgress("Done the whole test.\nConfig params: {0}", config.ToString());
            }
            catch (Exception exc)
            {
                LoadTestDriverBase.WriteProgress("{0} has failed due to an exception: {1}", TestName, exc.ToString());
                Environment.Exit(99);
            }
        }

        private static TimeSpan Divide(TimeSpan timeSpan, double value)
        {
            double ticksD = checked((double)timeSpan.Ticks / value);
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
        }
    }
}
