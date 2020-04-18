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
        private readonly ILocalGrainDirectory localGrainDirectory;

        public ClientGrainLocator(ILocalGrainDirectory localGrainDirectory)
        {
            this.localGrainDirectory = localGrainDirectory;
        }

        public async Task<List<ActivationAddress>> Lookup(GrainId grainId)
        {
            if (!ClientGrainId.TryParse(grainId, out var clientId))
            {
                ThrowNotClientGrainId(grainId);
            }

            var addresses = await this.localGrainDirectory.LookupAsync(clientId.GrainId);
            return addresses.Addresses;
        }

        public bool TryLocalLookup(GrainId grainId, out List<ActivationAddress> addresses)
        {
            if (!ClientGrainId.TryParse(grainId, out var clientId))
            {
                ThrowNotClientGrainId(grainId);
            }

            if (this.localGrainDirectory.LocalLookup(clientId.GrainId, out var addressesAndTag))
            {
                addresses = addressesAndTag.Addresses;
                return true;
            }

            addresses = null;
            return false;
        }

        public Task<ActivationAddress> Register(ActivationAddress address) => throw new InvalidOperationException($"Cannot register client grain explicitly");

        public Task Unregister(ActivationAddress address, UnregistrationCause cause) => throw new InvalidOperationException($"Cannot unregister client grain explicitly");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static GrainId ThrowNotClientGrainId(GrainId grainId) => throw new InvalidOperationException($"{grainId} is not a client id");
    }
}
