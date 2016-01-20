
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using Tester.TestStreamProviders.Generator;
using Tester.TestStreamProviders.Generator.Generators;
using TestGrainInterfaces;
using UnitTests.Grains;

namespace Tester.StreamingTests
{
    public class ImplicitSubscritionRecoverableStreamTestRunner
    {
        private readonly IGrainFactory grainFactory;
        private readonly string streamProviderTypeName;
        private readonly string streamProviderName;
        private readonly GeneratorAdapterConfig adapterConfig;

        public ImplicitSubscritionRecoverableStreamTestRunner(IGrainFactory grainFactory, string streamProviderTypeName, string streamProviderName, GeneratorAdapterConfig adapterConfig)
        {
            this.grainFactory = grainFactory;
            this.streamProviderTypeName = streamProviderTypeName;
            this.streamProviderName = streamProviderName;
            this.adapterConfig = adapterConfig;
        }

        public async Task Recoverable100EventStreamsWithTransientErrors(string streamNamespace)
        {
            try
            {
                var generatorConfig = new SimpleGeneratorConfig
                {
                    StreamNamespace = streamNamespace,
                    EventsInStream = 100
                };

                var mgmt = grainFactory.GetGrain<IManagementGrain>(0);
                object[] results = await mgmt.SendControlCommandToProvider(streamProviderTypeName, streamProviderName, (int)StreamGeneratorCommand.Configure, generatorConfig);
                Assert.AreEqual(2, results.Length, "expected responses");
                bool[] bResults = results.Cast<bool>().ToArray();
                foreach (var result in bResults)
                {
                    Assert.AreEqual(true, result, "Control command result");
                }

                await TestingUtils.WaitUntilAsync(assertIsTrue => CheckCounters(generatorConfig, generatorConfig.EventsInStream, assertIsTrue), TimeSpan.FromSeconds(30));
            }
            finally
            {
                var reporter = GrainClient.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                reporter.Reset().Ignore();
            }
        }

        public async Task Recoverable100EventStreamsWith1NonTransientError(string streamNamespace)
        {
            try
            {
                var generatorConfig = new SimpleGeneratorConfig
                {
                    StreamNamespace = streamNamespace,
                    EventsInStream = 100
                };

                var mgmt = grainFactory.GetGrain<IManagementGrain>(0);
                object[] results = await mgmt.SendControlCommandToProvider(streamProviderTypeName, streamProviderName, (int)StreamGeneratorCommand.Configure, generatorConfig);
                Assert.AreEqual(2, results.Length, "expected responses");
                bool[] bResults = results.Cast<bool>().ToArray();
                foreach (var result in bResults)
                {
                    Assert.AreEqual(true, result, "Control command result");
                }

                // should eventually skip the faulted event, so event count should be one (faulted event) less that number of events in stream.
                await TestingUtils.WaitUntilAsync(assertIsTrue => CheckCounters(generatorConfig, generatorConfig.EventsInStream - 1, assertIsTrue), TimeSpan.FromSeconds(90));
            }
            finally
            {
                var reporter = GrainClient.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                reporter.Reset().Ignore();
            }
        }

        private async Task<bool> CheckCounters(SimpleGeneratorConfig generatorConfig, int eventsInStream, bool assertIsTrue)
        {
            var reporter = grainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);

            var report = await reporter.GetReport(streamProviderName, generatorConfig.StreamNamespace);
            if (assertIsTrue)
            {
                // one stream per queue
                Assert.AreEqual(adapterConfig.TotalQueueCount, report.Count, "Stream count");
                foreach (int eventsPerStream in report.Values)
                {
                    Assert.AreEqual(eventsInStream, eventsPerStream, "Events per stream");
                }
            }
            else if (adapterConfig.TotalQueueCount != report.Count ||
                     report.Values.Any(count => count != eventsInStream))
            {
                return false;
            }
            return true;
        }
    }
}
