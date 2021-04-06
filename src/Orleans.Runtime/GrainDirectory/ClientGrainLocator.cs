using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.Internal;

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

        public async ValueTask<ActivationAddress> Lookup(GrainId grainId)
        {
            if (!ClientGrainId.TryParse(grainId, out _))
            {
                ThrowNotClientGrainId(grainId);
            }

            var results = await _clientDirectory.Lookup(grainId);
            return SelectAddress(results);
        }

        private ActivationAddress SelectAddress(List<ActivationAddress> results)
        {
            if (results is { Count: > 0 })
            {
                foreach (var location in results)
                {
                    if (location.Silo.Equals(_localSiloAddress))
                    {
                        return location;
                    }
                }

                return results[ThreadSafeRandom.Next(results.Count)];
            }

            return null;
        }

        public bool TryLocalLookup(GrainId grainId, out ActivationAddress address)
        {
            if (!ClientGrainId.TryParse(grainId, out _))
            {
                ThrowNotClientGrainId(grainId);
            }

            if (_clientDirectory.TryLocalLookup(grainId, out var addresses))
            {
                address = SelectAddress(addresses);
                return address is object;
            }

            address = null;
            return false;
        }

        public Task<ActivationAddress> Register(ActivationAddress address) => throw new InvalidOperationException($"Cannot register client grain explicitly");

        public Task Unregister(ActivationAddress address, UnregistrationCause cause) => throw new InvalidOperationException($"Cannot unregister client grain explicitly");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static GrainId ThrowNotClientGrainId(GrainId grainId) => throw new InvalidOperationException($"{grainId} is not a client id");
    }
}
