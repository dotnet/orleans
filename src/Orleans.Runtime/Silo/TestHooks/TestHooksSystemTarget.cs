using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Runtime.ConsistentRing;
using Orleans.Storage;
using Orleans.Statistics;
using Orleans.Runtime.Messaging;

#nullable disable
namespace Orleans.Runtime.TestHooks
{
    /// <summary>
    /// A fake, test-only implementation of <see cref="IEnvironmentStatisticsProvider"/>.
    /// </summary>
    internal class TestHooksEnvironmentStatisticsProvider : IEnvironmentStatisticsProvider
    {
        private static EnvironmentStatisticsProvider _realStatisticsProvider = new();

        private EnvironmentStatistics? _currentStats = null;

        public EnvironmentStatistics GetEnvironmentStatistics()
        {
            var stats = _currentStats ?? new();
            if (!stats.IsValid())
            {
                stats = _realStatisticsProvider.GetEnvironmentStatistics();
            }

            return stats;
        }

        public void LatchHardwareStatistics(EnvironmentStatistics stats) => _currentStats = stats;
        public void UnlatchHardwareStatistics() => _currentStats = null;
    }

    /// <summary>
    /// Test hook functions for white box testing implemented as a SystemTarget
    /// </summary>
    internal sealed class TestHooksSystemTarget : SystemTarget, ITestHooksSystemTarget
    {
        private readonly IServiceProvider serviceProvider;
        private readonly ISiloStatusOracle siloStatusOracle;

        private readonly IConsistentRingProvider consistentRingProvider;

        public TestHooksSystemTarget(
            IServiceProvider serviceProvider,
            ISiloStatusOracle siloStatusOracle,
            SystemTargetShared shared)
            : base(Constants.TestHooksSystemTargetType, shared)
        {
            this.serviceProvider = serviceProvider;
            this.siloStatusOracle = siloStatusOracle;
            this.consistentRingProvider = this.serviceProvider.GetRequiredService<IConsistentRingProvider>();
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        public void Initialize()
        {
            // No-op
        }

        public Task<SiloAddress> GetConsistentRingPrimaryTargetSilo(uint key)
        {
            return Task.FromResult(consistentRingProvider.GetPrimaryTargetSilo(key));
        }

        public Task<string> GetConsistentRingProviderDiagnosticInfo()
        {
            return Task.FromResult(consistentRingProvider.ToString()); 
        }
        
        public Task<string> GetServiceId() => Task.FromResult(this.serviceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value.ServiceId);

        public Task<bool> HasStorageProvider(string providerName)
        {
            return Task.FromResult(this.serviceProvider.GetKeyedService<IGrainStorage>(providerName) != null);
        }

        public Task<bool> HasStreamProvider(string providerName)
        {
            return Task.FromResult(this.serviceProvider.GetKeyedService<IGrainStorage>(providerName) != null);
        }

        public Task<int> UnregisterGrainForTesting(GrainId grain) => Task.FromResult(this.serviceProvider.GetRequiredService<Catalog>().UnregisterGrainForTesting(grain));

        public Task<Dictionary<SiloAddress, SiloStatus>> GetApproximateSiloStatuses() => Task.FromResult(this.siloStatusOracle.GetApproximateSiloStatuses());

        public async Task<bool> WaitForActiveSilos(SiloAddress[] expectedActiveSilos, TimeSpan timeout)
        {
            var expected = expectedActiveSilos.ToHashSet();
            if (ActiveSilosMatch(this.siloStatusOracle, expected))
            {
                return true;
            }

            if (timeout <= TimeSpan.Zero)
            {
                return false;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var listener = new ActiveSiloSetListener(this.siloStatusOracle, expected, completion);
            this.siloStatusOracle.SubscribeToSiloStatusEvents(listener);
            try
            {
                if (ActiveSilosMatch(this.siloStatusOracle, expected))
                {
                    return true;
                }

                await completion.Task.WaitAsync(timeout);
                return true;
            }
            catch (TimeoutException)
            {
                return ActiveSilosMatch(this.siloStatusOracle, expected);
            }
            finally
            {
                this.siloStatusOracle.UnSubscribeFromSiloStatusEvents(listener);
            }
        }

        public async Task<bool> WaitForClusterManifest(SiloAddress[] expectedSilos, TimeSpan timeout)
        {
            var expected = expectedSilos.ToHashSet();
            var manifestProvider = this.serviceProvider.GetRequiredService<IClusterManifestProvider>();
            if (ClusterManifestMatches(manifestProvider, expected))
            {
                return true;
            }

            if (timeout <= TimeSpan.Zero)
            {
                return false;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = ManifestEvents.AllEvents.Subscribe(new ClusterManifestUpdatedObserver(manifestProvider, expected, completion));

            if (ClusterManifestMatches(manifestProvider, expected))
            {
                return true;
            }

            try
            {
                await completion.Task.WaitAsync(timeout);
                return true;
            }
            catch (TimeoutException)
            {
                return ClusterManifestMatches(manifestProvider, expected);
            }
        }

        private static bool ActiveSilosMatch(ISiloStatusOracle siloStatusOracle, HashSet<SiloAddress> expectedActiveSilos)
        {
            return expectedActiveSilos.SetEquals(siloStatusOracle.GetApproximateSiloStatuses(onlyActive: true).Keys);
        }

        private static bool ClusterManifestMatches(IClusterManifestProvider manifestProvider, HashSet<SiloAddress> expectedSilos)
        {
            return expectedSilos.SetEquals(manifestProvider.Current.Silos.Keys);
        }

        private sealed class ActiveSiloSetListener : ISiloStatusListener
        {
            private readonly ISiloStatusOracle siloStatusOracle;
            private readonly HashSet<SiloAddress> expectedActiveSilos;
            private readonly TaskCompletionSource completion;

            public ActiveSiloSetListener(
                ISiloStatusOracle siloStatusOracle,
                HashSet<SiloAddress> expectedActiveSilos,
                TaskCompletionSource completion)
            {
                this.siloStatusOracle = siloStatusOracle;
                this.expectedActiveSilos = expectedActiveSilos;
                this.completion = completion;
            }

            public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
            {
                if (ActiveSilosMatch(this.siloStatusOracle, this.expectedActiveSilos))
                {
                    this.completion.TrySetResult();
                }
            }
        }

        private sealed class ClusterManifestUpdatedObserver(
            IClusterManifestProvider manifestProvider,
            HashSet<SiloAddress> expectedSilos,
            TaskCompletionSource completion) : IObserver<ManifestEvents.ManifestEvent>
        {
            public void OnCompleted()
            {
            }

            public void OnError(Exception error) => completion.TrySetException(error);

            public void OnNext(ManifestEvents.ManifestEvent value)
            {
                if (value is ManifestEvents.ClusterManifestUpdated update
                    && ReferenceEquals(update.Source, manifestProvider)
                    && expectedSilos.SetEquals(update.Manifest.Silos.Keys))
                {
                    completion.TrySetResult();
                }
            }
        }
    }
}
