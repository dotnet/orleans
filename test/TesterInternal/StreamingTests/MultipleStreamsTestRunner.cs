using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace UnitTests.Streaming
{
    public class MultipleStreamsTestRunner
    {
        public const string SMS_STREAM_PROVIDER_NAME = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
        public const string AQ_STREAM_PROVIDER_NAME = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        private Logger logger;
        private readonly IInternalGrainFactory grainFactory;
        private readonly string streamProviderName;
        private readonly int testNumber;
        private readonly bool runFullTest;

        internal MultipleStreamsTestRunner(IInternalGrainFactory grainFactory, string streamProvider, int testNum = 0, bool fullTest = true)
        {
            this.grainFactory = grainFactory;
            this.streamProviderName = streamProvider;
            this.logger = LogManager.GetLogger("MultipleStreamsTestRunner", LoggerType.Application);
            this.testNumber = testNum;
            this.runFullTest = fullTest;
        }

        private void Heading(string testName)
        {
            logger.Info("\n\n************************ {0}_{1}_{2} ********************************* \n\n", streamProviderName, testNumber, testName);
        }

        public async Task StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(Func<SiloHandle> startSiloFunc = null, Action<SiloHandle> stopSiloFunc = null)
        {
            Heading(String.Format("MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains"));
            List<SingleStreamTestRunner> runners = new List<SingleStreamTestRunner>();
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                runners.Add(new SingleStreamTestRunner(this.grainFactory, this.streamProviderName, i, runFullTest));
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
                silo = startSiloFunc();
            }

            foreach (var runner in runners)
            {
                tasks.Add(runner.BasicTestAsync(runFullTest));
            }
            await Task.WhenAll(tasks);
            tasks.Clear();

            if (stopSiloFunc != null)
            {
                logger.Info("\n\n\nAbout to stop silo  {0} \n\n", silo.SiloAddress);

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