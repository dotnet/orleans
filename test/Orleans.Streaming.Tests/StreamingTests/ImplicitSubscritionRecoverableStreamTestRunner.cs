using Orleans.TestingHost.Utils;
using TestGrainInterfaces;
using UnitTests.Grains;
using Xunit;

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

        public async Task Recoverable100EventStreamsWithTransientErrors(
            Func<string, int, int, CancellationToken, Task> generateFn,
            string streamNamespace,
            int streamCount,
            int eventsInStream,
            CancellationToken cancellationToken)
        {
            try
            {
                await generateFn(streamNamespace, streamCount, eventsInStream, cancellationToken);
                await TestingUtils.WaitUntilAsync(
                    (lastTry, token) => this.CheckCounters(streamNamespace, streamCount, eventsInStream, lastTry, token),
                    TimeSpan.FromSeconds(30),
                    cancellationToken: cancellationToken);
            }
            finally
            {
                var reporter = this.grainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await reporter.Reset(cleanup.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        public Task Recoverable100EventStreamsWithTransientErrors(
            Func<string, int, int, Task> generateFn,
            string streamNamespace,
            int streamCount,
            int eventsInStream) =>
            Recoverable100EventStreamsWithTransientErrors(
                (namespaceValue, count, eventCount, _) =>
                    generateFn(namespaceValue, count, eventCount),
                streamNamespace,
                streamCount,
                eventsInStream,
                CancellationToken.None);

        public async Task Recoverable100EventStreamsWith1NonTransientError(
            Func<string, int, int, CancellationToken, Task> generateFn,
            string streamNamespace,
            int streamCount,
            int eventsInStream,
            CancellationToken cancellationToken)
        {
            try
            {
                await generateFn(streamNamespace, streamCount, eventsInStream, cancellationToken);
                // should eventually skip the faulted event, so event count should be one (faulted event) less that number of events in stream.
                await TestingUtils.WaitUntilAsync(
                    (lastTry, token) => this.CheckCounters(streamNamespace, streamCount, eventsInStream - 1, lastTry, token),
                    TimeSpan.FromSeconds(90),
                    cancellationToken: cancellationToken);
            }
            finally
            {
                var reporter = this.grainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await reporter.Reset(cleanup.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        public Task Recoverable100EventStreamsWith1NonTransientError(
            Func<string, int, int, Task> generateFn,
            string streamNamespace,
            int streamCount,
            int eventsInStream) =>
            Recoverable100EventStreamsWith1NonTransientError(
                (namespaceValue, count, eventCount, _) =>
                    generateFn(namespaceValue, count, eventCount),
                streamNamespace,
                streamCount,
                eventsInStream,
                CancellationToken.None);

        private async Task<bool> CheckCounters(string streamNamespace, int streamCount, int eventsInStream, bool assertIsTrue, CancellationToken cancellationToken)
        {
            var reporter = grainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);

            var report = await reporter.GetReport(streamProviderName, streamNamespace, cancellationToken);
            if (assertIsTrue)
            {
                // one stream per queue
                Assert.Equal(streamCount, report.Count);
                foreach (int eventsPerStream in report.Values)
                {
                    Assert.Equal(eventsInStream, eventsPerStream);
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
