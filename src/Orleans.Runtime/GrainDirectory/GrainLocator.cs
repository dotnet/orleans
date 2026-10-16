using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Provides functionality for locating grain activations in a cluster and registering the location of grain activations.
    /// </summary>
    internal class GrainLocator
    {
        private readonly GrainLocatorResolver _grainLocatorResolver;
        private readonly DirectoryInstruments _directoryInstruments;

        public GrainLocator(GrainLocatorResolver grainLocatorResolver, DirectoryInstruments directoryInstruments)
        {
            _grainLocatorResolver = grainLocatorResolver;
            _directoryInstruments = directoryInstruments;
        }

        public ValueTask<GrainAddress?> Lookup(GrainId grainId) => GetGrainLocator(grainId.Type).Lookup(grainId);

        public async Task<GrainAddress?> Register(
            GrainAddress address,
            GrainAddress? previousRegistration,
            CancellationToken cancellationToken = default)
        {
            var grainLocator = GetGrainLocator(address.GrainId.Type);
            var metrics = RegistrationMetricTracker.Start(_directoryInstruments, grainLocator);
            try
            {
                var result = grainLocator is CachedGrainLocator cachedGrainLocator
                    ? await cachedGrainLocator.Register(address, previousRegistration, cancellationToken)
                    : await grainLocator.Register(address, previousRegistration).WaitAsync(cancellationToken);
                metrics.RecordSucceeded();
                return result;
            }
            catch (OperationCanceledException)
            {
                metrics.RecordCanceled();
                throw;
            }
            catch
            {
                metrics.RecordFailed();
                throw;
            }
        }

        public Task Unregister(GrainAddress address, UnregistrationCause cause) => GetGrainLocator(address.GrainId.Type).Unregister(address, cause);

        public bool TryLookupInCache(GrainId grainId, [NotNullWhen(true)] out GrainAddress? address) => GetGrainLocator(grainId.Type).TryLookupInCache(grainId, out address);

        internal bool TryGetCacheEntry(
            GrainId grainId,
            SiloAddress siloAddress,
            [NotNullWhen(true)] out GrainDirectoryCacheEntry? entry)
        {
            var grainLocator = GetGrainLocator(grainId.Type);
            return grainLocator switch
            {
                CachedGrainLocator cached => cached.TryGetCacheEntry(grainId, siloAddress, out entry),
                DhtGrainLocator dht => dht.TryGetCacheEntry(grainId, siloAddress, out entry),
                _ => ReturnFalse(out entry),
            };

            static bool ReturnFalse(out GrainDirectoryCacheEntry? result)
            {
                result = null;
                return false;
            }
        }

        public void InvalidateCache(GrainId grainId) => GetGrainLocator(grainId.Type).InvalidateCache(grainId);

        public void InvalidateCache(GrainAddress address) => GetGrainLocator(address.GrainId.Type).InvalidateCache(address);

        private IGrainLocator GetGrainLocator(GrainType grainType) => _grainLocatorResolver.GetGrainLocator(grainType);

        private static string GetLocatorTag(IGrainLocator grainLocator) => grainLocator switch
        {
            CachedGrainLocator => "cached",
            ClientGrainLocator => "client",
            DhtGrainLocator => "dht",
            _ => "custom"
        };

        private readonly struct RegistrationMetricTracker
        {
            private readonly DirectoryInstruments? _directoryInstruments;
            private readonly ValueStopwatch _stopwatch;
            private readonly string? _locator;

            private RegistrationMetricTracker(DirectoryInstruments directoryInstruments, ValueStopwatch stopwatch, string locator)
            {
                _directoryInstruments = directoryInstruments;
                _stopwatch = stopwatch;
                _locator = locator;
            }

            public static RegistrationMetricTracker Start(DirectoryInstruments directoryInstruments, IGrainLocator grainLocator)
            {
                return directoryInstruments.RegistrationMetricsEnabled
                    ? new(directoryInstruments, ValueStopwatch.StartNew(), GetLocatorTag(grainLocator))
                    : default;
            }

            public void RecordSucceeded() => Record(DirectoryInstruments.RegistrationStatusSuccess);

            public void RecordCanceled() => Record(DirectoryInstruments.RegistrationStatusCanceled);

            public void RecordFailed() => Record(DirectoryInstruments.RegistrationStatusError);

            private void Record(string status)
            {
                if (_directoryInstruments is null || _locator is null)
                {
                    return;
                }

                _directoryInstruments.OnRegistrationCompleted(_stopwatch.Elapsed, _locator, status);
            }
        }

        public void UpdateCache(GrainId grainId, SiloAddress siloAddress) => GetGrainLocator(grainId.Type).UpdateCache(grainId, siloAddress);

        public void UpdateCache(GrainAddressCacheUpdate update)
        {
            if (update.ValidGrainAddress is { } validAddress)
            {
                Debug.Assert(validAddress.SiloAddress is not null);
                UpdateCache(validAddress.GrainId, validAddress.SiloAddress);
            }
            else
            {
                InvalidateCache(update.InvalidGrainAddress);
            }
        }
    }
}
