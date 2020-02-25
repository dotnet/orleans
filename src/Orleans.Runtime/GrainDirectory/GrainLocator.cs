using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Implementation of <see cref="IGrainLocator"/> that uses an <see cref="IGrainDirectory"/> store.
    /// </summary>
    internal class GrainLocator : IGrainLocator
    {
        private readonly IGrainDirectory grainDirectory;
        private readonly InClusterGrainLocator inClusterGrainLocator;
        private readonly IGrainDirectoryCache cache;

        public GrainLocator(IGrainDirectory grainDirectory, InClusterGrainLocator inClusterGrainLocator)
        {
            this.grainDirectory = grainDirectory;
            this.inClusterGrainLocator = inClusterGrainLocator;
            this.cache = new LRUBasedGrainDirectoryCache(GrainDirectoryOptions.DEFAULT_CACHE_SIZE, GrainDirectoryOptions.DEFAULT_MAXIMUM_CACHE_TTL);
        }

        public async Task<List<ActivationAddress>> Lookup(GrainId grainId)
        {
            if (grainId.IsClient)
                return await this.inClusterGrainLocator.Lookup(grainId);

            var results = new List<ActivationAddress>();

            var entry = await this.grainDirectory.Lookup(grainId.ToParsableString());

            if (entry == null)
                return results;

            results.Add(ConvertToActivationAddress(entry));
            this.cache.AddOrUpdate(grainId, results.Select(item => Tuple.Create(item.Silo, item.Activation)).ToList(), 0);

            return results;
        }

        public async Task<ActivationAddress> Register(ActivationAddress address)
        {
            if (address.Grain.IsClient)
                return await this.inClusterGrainLocator.Register(address);

            var result = await this.grainDirectory.Register(ConvertToGrainAddress(address));
            var activationAddress = ConvertToActivationAddress(result);
            this.cache.AddOrUpdate(
                activationAddress.Grain,
                new List<Tuple<SiloAddress,ActivationId>>() { Tuple.Create(activationAddress.Silo, activationAddress.Activation) },
                0);
            return activationAddress;
        }

        public bool TryLocalLookup(GrainId grainId, out List<ActivationAddress> addresses)
        {
            if (grainId.IsClient)
                return this.inClusterGrainLocator.TryLocalLookup(grainId, out addresses);

            if (this.cache.LookUp(grainId, out var results))
            {
                addresses = results
                    .Select(tuple => ActivationAddress.GetAddress(tuple.Item1, grainId, tuple.Item2))
                    .ToList();
                return true;
            }

            addresses = null;
            return false;
        }

        public async Task Unregister(ActivationAddress address, UnregistrationCause cause)
        {
            if (address.Grain.IsClient)
                await this.inClusterGrainLocator.Unregister(address, cause);

            await this.grainDirectory.Unregister(ConvertToGrainAddress(address));
            this.cache.Remove(address.Grain);
        }

        public async Task UnregisterMany(List<ActivationAddress> addresses, UnregistrationCause cause)
        {
            var tasks = addresses.Select(addr => Unregister(addr, cause)).ToList();
            await Task.WhenAll(tasks);
            foreach (var addr in addresses)
                this.cache.Remove(addr.Grain);
        }

        private static ActivationAddress ConvertToActivationAddress(GrainAddress addr)
        {
            try
            {
                return ActivationAddress.GetAddress(
                        SiloAddress.FromParsableString(addr.SiloAddress),
                        GrainId.FromParsableString(addr.GrainId),
                        ActivationId.GetActivationId(UniqueKey.Parse(addr.ActivationId.AsSpan())));
            }
            catch (Exception)
            {

                throw;
            }
        }

        private static GrainAddress ConvertToGrainAddress(ActivationAddress addr)
        {
            return new GrainAddress
            {
                SiloAddress = addr.Silo.ToParsableString(),
                GrainId = addr.Grain.ToParsableString(),
                ActivationId = (addr.Activation.Key.ToHexString())
            };
        }
    }
}
