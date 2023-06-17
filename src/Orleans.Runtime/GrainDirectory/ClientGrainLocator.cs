using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Implementation of <see cref="IGrainLocator"/> that uses the in memory distributed directory of Orleans
    /// </summary>
    internal class ClientGrainLocator : IGrainLocator
    {
        private readonly SiloAddress _localSiloAddress;
        private readonly ILocalClientDirectory _clientDirectory;

        public ClientGrainLocator(ILocalSiloDetails localSiloDetails, ILocalClientDirectory clientDirectory)
        {
            _localSiloAddress = localSiloDetails.SiloAddress;
            _clientDirectory = clientDirectory;
        }

        public async ValueTask<GrainAddress> Lookup(GrainId grainId)
        {
            if (!ClientGrainId.TryParse(grainId, out var clientGrainId))
            {
                ThrowNotClientGrainId(grainId);
            }

            var results = await _clientDirectory.Lookup(clientGrainId.GrainId);
            return SelectAddress(results, grainId);
        }

        private GrainAddress SelectAddress(List<GrainAddress> results, GrainId grainId)
        {
            GrainAddress unadjustedResult = null;
            if (results is { Count: > 0 })
            {
                foreach (var location in results)
                {
                    if (location.SiloAddress.Equals(_localSiloAddress))
                    {
                        unadjustedResult = location;
                        break;
                    }
                }

                unadjustedResult = results[Random.Shared.Next(results.Count)];
            }

            if (unadjustedResult is not null)
            {
                return GrainAddress.GetAddress(unadjustedResult.SiloAddress, grainId, unadjustedResult.ActivationId);
            }

            return null;
        }

        public Task<GrainAddress> Register(GrainAddress address, GrainAddress previousAddress) => throw new InvalidOperationException($"Cannot register client grain explicitly");

        public Task Unregister(GrainAddress address, UnregistrationCause cause) => throw new InvalidOperationException($"Cannot unregister client grain explicitly");

        private static void ThrowNotClientGrainId(GrainId grainId) => throw new InvalidOperationException($"{grainId} is not a client id");

        public void CachePlacementDecision(GrainId grainId, SiloAddress siloAddress) { }

        public void InvalidateCache(GrainId grainId) { }

        public void InvalidateCache(GrainAddress address) { }

        public bool TryLookupInCache(GrainId grainId, out GrainAddress address)
        {
            if (!ClientGrainId.TryParse(grainId, out var clientGrainId))
            {
                ThrowNotClientGrainId(grainId);
            }

            if (_clientDirectory.TryLocalLookup(clientGrainId.GrainId, out var addresses))
            {
                address = SelectAddress(addresses, grainId);
                return address is not null;
            }

            address = null;
            return false;
        }
    }
}
