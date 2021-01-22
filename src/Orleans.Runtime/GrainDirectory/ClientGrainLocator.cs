using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Implementation of <see cref="IGrainLocator"/> that uses the in memory distributed directory of Orleans
    /// </summary>
    internal class ClientGrainLocator : IGrainLocator
    {
        private readonly ILocalClientDirectory _clientDirectory;

        public ClientGrainLocator(ILocalClientDirectory clientDirectory)
        {
            _clientDirectory = clientDirectory;
        }

        public async Task<List<ActivationAddress>> Lookup(GrainId grainId)
        {
            if (!ClientGrainId.TryParse(grainId, out _))
            {
                ThrowNotClientGrainId(grainId);
            }

            return await _clientDirectory.Lookup(grainId);
        }

        public bool TryLocalLookup(GrainId grainId, out List<ActivationAddress> addresses)
        {
            if (!ClientGrainId.TryParse(grainId, out _))
            {
                ThrowNotClientGrainId(grainId);
            }

            return _clientDirectory.TryLocalLookup(grainId, out addresses);
        }

        public Task<ActivationAddress> Register(ActivationAddress address) => throw new InvalidOperationException($"Cannot register client grain explicitly");

        public Task Unregister(ActivationAddress address, UnregistrationCause cause) => throw new InvalidOperationException($"Cannot unregister client grain explicitly");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static GrainId ThrowNotClientGrainId(GrainId grainId) => throw new InvalidOperationException($"{grainId} is not a client id");
    }
}
