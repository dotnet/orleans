using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Implementation of <see cref="IGrainLocator"/> that uses the in memory distributed directory of Orleans
    /// </summary>
    internal class DhtGrainLocator : IGrainLocator
    {
        private readonly ILocalGrainDirectory localGrainDirectory;

        public DhtGrainLocator(ILocalGrainDirectory localGrainDirectory)
        {
            this.localGrainDirectory = localGrainDirectory;
        }

        public async Task<List<ActivationAddress>> Lookup(GrainId grainId)
            => (await this.localGrainDirectory.LookupAsync(grainId)).Addresses;

        public bool TryLocalLookup(GrainId grainId, out List<ActivationAddress> addresses)
        {
            if (this.localGrainDirectory.LocalLookup(grainId, out var addressesAndTag))
            {
                addresses = addressesAndTag.Addresses;
                return true;
            }
            addresses = null;
            return false;
        }

        public async Task<ActivationAddress> Register(ActivationAddress address)
            => (await this.localGrainDirectory.RegisterAsync(address, singleActivation: true)).Address;

        public  Task Unregister(ActivationAddress address, UnregistrationCause cause)
            => this.localGrainDirectory.UnregisterAsync(address, cause);

        public Task UnregisterMany(List<ActivationAddress> addresses, UnregistrationCause cause)
            => this.localGrainDirectory.UnregisterManyAsync(addresses, cause);
    }
}
