using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

        public GrainLocator(GrainLocatorResolver grainLocatorResolver)
        {
            _grainLocatorResolver = grainLocatorResolver;
        }

        public ValueTask<GrainAddress?> Lookup(GrainId grainId) => GetGrainLocator(grainId.Type).Lookup(grainId);

        public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousRegistration)
        {
            var grainLocator = GetGrainLocator(address.GrainId.Type);
            var metrics = RegistrationMetricTracker.Start(grainLocator);
            try
            {
                var result = await grainLocator.Register(address, previousRegistration);
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
            private readonly ValueStopwatch _stopwatch;
            private readonly string? _locator;

            private RegistrationMetricTracker(ValueStopwatch stopwatch, string locator)
            {
                _stopwatch = stopwatch;
                _locator = locator;
            }

            public static RegistrationMetricTracker Start(IGrainLocator grainLocator)
            {
                return DirectoryInstruments.RegistrationMetricsEnabled
                    ? new(ValueStopwatch.StartNew(), GetLocatorTag(grainLocator))
                    : default;
            }

            public void RecordSucceeded() => Record(DirectoryInstruments.RegistrationStatusSuccess);

            public void RecordCanceled() => Record(DirectoryInstruments.RegistrationStatusCanceled);

            public void RecordFailed() => Record(DirectoryInstruments.RegistrationStatusError);

            private void Record(string status)
            {
                if (_locator is null)
                {
                    return;
                }

                DirectoryInstruments.OnRegistrationCompleted(_stopwatch.Elapsed, _locator, status);
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
