using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using LoadTestGrainInterfaces;

using Orleans;
using Orleans.Runtime;

namespace StreamPullingAgentBenchmark.EmbeddedSiloLoadTest
{
    /// <summary>
    /// Base class for writing load tests that can optionally use embedded silos.
    /// </summary>
    /// <remarks>
    /// The tests are broken up into "phases" (warm-up and average). Within each "phase", there are
    /// "periods", where the test should report its results. At the end of a phase, the test should
    /// report the aggregate results across the entire phase.
    /// </remarks>
    /// <typeparam name="TOptions">Type of the options used by the test</typeparam>
    public abstract class BaseEmbeddedSiloLoadTest<TOptions> where TOptions : BaseOptions, new()
    {
        private static readonly TimeSpan MINIMUM_POLLING_PERIOD = TimeSpan.FromSeconds(30);

        protected static TOptions _options { get; private set; }

        /// <summary>
        /// Runs the test
        /// </summary>
        /// <param name="args">Main method's args</param>
        /// <returns>Status code</returns>
        public async Task<int> RunAsync(string[] args)
        {
            Utilities.LogAlways(string.Format("Started with arguments: {0}.", LoadTestGrainInterfaces.Utils.EnumerableToString(args)));

            TOptions options;
            if (!Utilities.ParseArguments(args, out options))
            {
                return 1;
            }
            _options = options;

            TimeSpan pollingPeriod = TimeSpan.FromSeconds(_options.PollingPeriodSeconds);
            TimeSpan warmUpDuration = TimeSpan.FromSeconds(_options.WarmUpSeconds);
            TimeSpan testDuration = TimeSpan.FromSeconds(_options.TestLengthSeconds);

            if (pollingPeriod < MINIMUM_POLLING_PERIOD)
            {
                throw new ArgumentOutOfRangeException("polling-period", _options.PollingPeriodSeconds, string.Format("Polling period must be at least {0}", MINIMUM_POLLING_PERIOD.TotalSeconds));
            }

            IEnumerable<AppDomain> hostDomains = null;
            if (_options.EmbedSilos > 0)
            {
                hostDomains = EmbeddedSiloManager.StartEmbeddedSilos(_options, args);
            }

            GrainClient.Initialize(_options.ClientConfigFile);
            Utilities.LogIfVerbose("Client is initialized.\n", _options);

            await InitializeAsync();

            await RunPhaseAsync("Warm-up", pollingPeriod, warmUpDuration);
            await RunPhaseAsync("Average", pollingPeriod, testDuration);

            await CleanupAsync();

            // signal the framework to that the test is finished.
            Utilities.LogAlways("Done the whole test.\n");

            if (hostDomains != null)
            {
                EmbeddedSiloManager.StopEmbeddedSilos(hostDomains);
            }

            return 0;
        }

        /// <summary>
        /// Called before the testing begins. Use this to start generating load if the test requires
        /// it.
        /// </summary>
        /// <returns></returns>
        protected virtual Task InitializeAsync()
        {
            return StartMockProvider();
        }

        /// <summary>
        /// Called after the testing ends. Use this to stop generating load.
        /// </summary>
        /// <returns></returns>
        protected virtual Task CleanupAsync()
        {
            return TaskDone.Done;
        }

        /// <summary>
        /// Called as a testing phase begins. Use this to reset any aggregate counters.
        /// </summary>
        /// <param name="phaseName">Name of the phase that is beginning.</param>
        /// <returns></returns>
        protected virtual Task StartPhaseAsync(string phaseName)
        {
            return TaskDone.Done;
        }

        /// <summary>
        /// Called at regular intervals during the phase. This should report aggregates that
        /// occurred during the period.
        /// </summary>
        /// <param name="phaseName">Name of the phase the period is in.</param>
        /// <param name="iterationCount">The period number.</param>
        /// <param name="duration">How long the period lasted.</param>
        /// <returns></returns>
        protected virtual Task PollPeriodAsync(string phaseName, int iterationCount, TimeSpan duration)
        {
            return TaskDone.Done;
        }

        /// <summary>
        /// Called as a testing phase ends. Use this to report aggregates that occured during the
        /// phase.
        /// </summary>
        /// <param name="phaseName">Name of the phase that is ending.</param>
        /// <param name="duration">How long the phase lasted.</param>
        /// <returns></returns>
        protected virtual Task EndPhaseAsync(string phaseName, TimeSpan duration)
        {
            return TaskDone.Done;
        }

        private async Task RunPhaseAsync(string phaseName, TimeSpan period, TimeSpan duration)
        {
            DateTime phaseBegin = DateTime.UtcNow;
            DateTime periodBegin = phaseBegin;

            await StartPhaseAsync(phaseName);

            int iterations = (int)Math.Ceiling(duration.TotalSeconds / period.TotalSeconds);
            for (int iteration = 0; iteration < iterations; ++iteration)
            {
                await Task.Delay(period);

                DateTime now = DateTime.UtcNow;
                TimeSpan periodDuration = now - periodBegin;
                periodBegin = now;
               await PollPeriodAsync(phaseName, iteration, periodDuration);
            }

            TimeSpan phaseDuration = periodBegin - phaseBegin;
            await EndPhaseAsync(phaseName, phaseDuration);
        }

        private static Task StartMockProvider()
        {
            IMockStreamProviderControlGrain g = MockStreamProviderControlGrainFactory.GetGrain(0);
            return g.StartProducing();
        }
    }
}
