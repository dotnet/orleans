using Microsoft.Extensions.Logging;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using UnitTests.StreamingTests;

namespace UnitTests.Streaming
{
    public class MultipleStreamsTestRunner
    {
        public const string SMS_STREAM_PROVIDER_NAME = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
        public const string AQ_STREAM_PROVIDER_NAME = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        private readonly ILogger logger;
        private readonly string streamProviderName;
        private readonly int testNumber;
        private readonly bool runFullTest;
        private readonly IInternalClusterClient client;

        internal MultipleStreamsTestRunner(IInternalClusterClient client, string streamProvider, int testNum = 0, bool fullTest = true)
        {
            this.client = client;
            this.streamProviderName = streamProvider;
            this.logger = (TestingUtils.CreateDefaultLoggerFactory($"{this.GetType().Name}.log")).CreateLogger<MultipleStreamsTestRunner>();
            this.testNumber = testNum;
            this.runFullTest = fullTest;
        }

        private void Heading(string testName)
        {
            logger.LogInformation("\n\n************************ {StreamProviderName}_{TestNumber}_{TestName} ********************************* \n\n", streamProviderName, testNumber, testName);
        }

        public async Task StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(Func<bool,SiloHandle> startSiloFunc = null, Action<SiloHandle> stopSiloFunc = null)
        {
            Heading("MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains");
            List<SingleStreamTestRunner> runners = new List<SingleStreamTestRunner>();
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                runners.Add(new SingleStreamTestRunner(this.client, this.streamProviderName, i, runFullTest));
            }
            foreach (var runner in runners)
            {
                tasks.Add(runner.StreamTest_Create_OneProducerGrainOneConsumerGrain());
            }
            await Task.WhenAll(tasks);
            tasks.Clear();

            SiloHandle silo = null;
            if (startSiloFunc != null)
            {
                silo = startSiloFunc(false);
            }

            foreach (var runner in runners)
            {
                tasks.Add(runner.BasicTestAsync(runFullTest));
            }
            await Task.WhenAll(tasks);
            tasks.Clear();

            if (stopSiloFunc != null)
            {
                logger.LogInformation("\n\n\nAbout to stop silo {SiloAddress} \n\n", silo.SiloAddress);

                stopSiloFunc(silo);

                foreach (var runner in runners)
                {
                    tasks.Add(runner.BasicTestAsync(runFullTest));
                }
                await Task.WhenAll(tasks);
                tasks.Clear();
            }

            foreach (var runner in runners)
            {
                tasks.Add(runner.StopProxies());
            }
            await Task.WhenAll(tasks);
        }
    }
}