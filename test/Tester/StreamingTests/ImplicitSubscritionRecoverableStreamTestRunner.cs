
using System;
using System.Linq;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.TestingHost;
using TestGrainInterfaces;
using UnitTests.Grains;

namespace Tester.StreamingTests
{
    public class ImplicitSubscritionRecoverableStreamTestRunner
    {
        private readonly IGrainFactory grainFactory;
        private readonly string streamProviderName;

        public ImplicitSubscritionRecoverableStreamTestRunner(IGrainFactory grainFactory, string streamProviderName)
        {
            this.grainFactory = grainFactory;
            this.streamProviderName = streamProviderName;
        }

        public async Task Recoverable100EventStreamsWithTransientErrors(Func<string, int, int, Task> generateFn, string streamNamespace, int streamCount, int eventsInStream)
        {
            try
            {
                await generateFn(streamNamespace, streamCount, eventsInStream);
                await TestingUtils.WaitUntilAsync(assertIsTrue => CheckCounters(streamNamespace, streamCount, eventsInStream, assertIsTrue), TimeSpan.FromSeconds(30));
            }
            finally
            {
                var reporter = GrainClient.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                reporter.Reset().Ignore();
            }
        }

        public async Task Recoverable100EventStreamsWith1NonTransientError(Func<string, int, int, Task> generateFn, string streamNamespace, int streamCount, int eventsInStream)
        {
            try
            {
                await generateFn(streamNamespace, streamCount, eventsInStream);
                // should eventually skip the faulted event, so event count should be one (faulted event) less that number of events in stream.
                await TestingUtils.WaitUntilAsync(assertIsTrue => CheckCounters(streamNamespace, streamCount, eventsInStream - 1, assertIsTrue), TimeSpan.FromSeconds(90));
            }
            finally
            {
                var reporter = GrainClient.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                reporter.Reset().Ignore();
            }
        }

        private async Task<bool> CheckCounters(string streamNamespace, int streamCount, int eventsInStream, bool assertIsTrue)
        {
            var reporter = grainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);

            var report = await reporter.GetReport(streamProviderName, streamNamespace);
            if (assertIsTrue)
            {
                // one stream per queue
                Assert.AreEqual(streamCount, report.Count, "Stream count");
                foreach (int eventsPerStream in report.Values)
                {
                    Assert.AreEqual(eventsInStream, eventsPerStream, "Events per stream");
                }
            }
            else if (streamCount != report.Count ||
                     report.Values.Any(count => count != eventsInStream))
            {
                return false;
            }
            return true;
        }
    }
}
