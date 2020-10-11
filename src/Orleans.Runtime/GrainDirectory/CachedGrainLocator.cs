using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Internal;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Implementation of <see cref="IGrainLocator"/> that uses <see cref="IGrainDirectory"/> stores.
    /// </summary>
    internal class CachedGrainLocator : IGrainLocator, ILifecycleParticipant<ISiloLifecycle>, CachedGrainLocator.ITestAccessor
    {
        private readonly GrainDirectoryResolver grainDirectoryResolver;
        private readonly IGrainDirectoryCache cache;

        private readonly CancellationTokenSource shutdownToken = new CancellationTokenSource();
        private readonly IClusterMembershipService clusterMembershipService;

        private HashSet<SiloAddress> knownDeadSilos = new HashSet<SiloAddress>();

        private Task listenToClusterChangeTask;

        internal interface ITestAccessor
        {
            MembershipVersion LastMembershipVersion { get; set; }
        }

        MembershipVersion ITestAccessor.LastMembershipVersion { get; set; }

        public CachedGrainLocator(
            GrainDirectoryResolver grainDirectoryResolver,
            IClusterMembershipService clusterMembershipService)
        {
            this.grainDirectoryResolver = grainDirectoryResolver;
            this.clusterMembershipService = clusterMembershipService;
            this.cache = new LRUBasedGrainDirectoryCache(GrainDirectoryOptions.DEFAULT_CACHE_SIZE, GrainDirectoryOptions.DEFAULT_MAXIMUM_CACHE_TTL);
        }

        public async Task<List<ActivationAddress>> Lookup(GrainId grainId)
        {
            var grainType = grainId.Type;
            if (grainType.IsClient() || grainType.IsSystemTarget())
            {
                ThrowUnsupportedGrainType(grainId);
            }

            List<ActivationAddress> results;

            // Check cache first
            if (TryLocalLookup(grainId, out results))
            {
                return results;
            }

            results = new List<ActivationAddress>();

            var entry = await GetGrainDirectory(grainId.Type).Lookup(grainId.ToString());

            // Nothing found
            if (entry == null)
                return results;

            var activationAddress = entry.ToActivationAddress();

            // Check if the entry is pointing to a dead silo
            if (this.knownDeadSilos.Contains(activationAddress.Silo))
            {
                // Remove it from the directory
                await GetGrainDirectory(grainId.Type).Unregister(entry);
            }
            else
            {
                // Add to the local cache and return it
                results.Add(activationAddress);
                this.cache.AddOrUpdate(grainId, new List<Tuple<SiloAddress, ActivationId>> { Tuple.Create(activationAddress.Silo, activationAddress.Activation) }, 0);
            }

            return results;
        }

        public async Task<ActivationAddress> Register(ActivationAddress address)
        {
            var grainType = address.Grain.Type;
            if (grainType.IsClient() || grainType.IsSystemTarget())
            {
                ThrowUnsupportedGrainType(address.Grain);
            }

            var grainAddress = address.ToGrainAddress();

            var result = await GetGrainDirectory(grainType).Register(grainAddress);
            var activationAddress = result.ToActivationAddress();

            // Check if the entry point to a dead silo
            if (this.knownDeadSilos.Contains(activationAddress.Silo))
            {
                // Remove outdated entry and retry to register
                await GetGrainDirectory(grainType).Unregister(result);
                result = await GetGrainDirectory(grainType).Register(grainAddress);
                activationAddress = result.ToActivationAddress();
            }

            // Cache update
            this.cache.AddOrUpdate(
                activationAddress.Grain,
                new List<Tuple<SiloAddress, ActivationId>>() { Tuple.Create(activationAddress.Silo, activationAddress.Activation) },
                0);

            return activationAddress;
        }

        public bool TryLocalLookup(GrainId grainId, out List<ActivationAddress> addresses)
        {
            var grainType = grainId.Type;
            if (grainType.IsClient() || grainType.IsSystemTarget())
            {
                ThrowUnsupportedGrainType(grainId);
            }

            if (this.cache.LookUp(grainId, out var results))
            {
                // IGrainDirectory only supports single activation
                var result = results[0];

                // If the silo is dead, remove the entry
                if (this.knownDeadSilos.Contains(result.Item1))
                {
                    this.cache.Remove(grainId);
                }
                else
                {
                    // Entry found and valid -> return it
                    addresses = new List<ActivationAddress>() { ActivationAddress.GetAddress(result.Item1, grainId, result.Item2) };
                    return true;
                }
            }

            addresses = null;
            return false;
        }

        public async Task Unregister(ActivationAddress address, UnregistrationCause cause)
        {
            try
            {
                await GetGrainDirectory(address.Grain.Type).Unregister(address.ToGrainAddress());
            }
            finally
            {
                this.cache.Remove(address.Grain);
            }
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            Task OnStart(CancellationToken ct)
            {
                this.listenToClusterChangeTask = ListenToClusterChange();
                return Task.CompletedTask;
            };
            async Task OnStop(CancellationToken ct)
            {
                this.shutdownToken.Cancel();
                if (listenToClusterChangeTask != default && !ct.IsCancellationRequested)
                    await listenToClusterChangeTask.WithCancellation(ct);
            };
            lifecycle.Subscribe(nameof(CachedGrainLocator), ServiceLifecycleStage.RuntimeGrainServices, OnStart, OnStop);
        }

        private IGrainDirectory GetGrainDirectory(GrainType grainType) => this.grainDirectoryResolver.Resolve(grainType);

        private async Task ListenToClusterChange()
        {
            var previousSnapshot = this.clusterMembershipService.CurrentSnapshot;
            // Update the list of known dead silos for lazy filtering for the first time
            this.knownDeadSilos = new HashSet<SiloAddress>(previousSnapshot.Members.Values
                .Where(m => m.Status == SiloStatus.Dead)
                .Select(m => m.SiloAddress));

            ((ITestAccessor)this).LastMembershipVersion = previousSnapshot.Version;

            var updates = this.clusterMembershipService.MembershipUpdates.WithCancellation(this.shutdownToken.Token);
            await foreach (var snapshot in updates)
            {
                // Update the list of known dead silos for lazy filtering
                this.knownDeadSilos = new HashSet<SiloAddress>(snapshot.Members.Values
                    .Where(m => m.Status.IsTerminating())
                    .Select(m => m.SiloAddress));

                // Active filtering: detect silos that went down and try to clean proactively the directory
                var changes = snapshot.CreateUpdate(previousSnapshot).Changes;
                var deadSilos = changes
                    .Where(member => member.Status.IsTerminating())
                    .Select(member => member.SiloAddress.ToParsableString())
                    .ToList();

                if (deadSilos.Count > 0)
                {
                    var tasks = new List<Task>();
                    foreach (var directory in this.grainDirectoryResolver.Directories)
                    {
                        tasks.Add(directory.UnregisterSilos(deadSilos));
                    }
                    await Task.WhenAll(tasks).WithCancellation(this.shutdownToken.Token);
                }

                ((ITestAccessor)this).LastMembershipVersion = snapshot.Version;
            }
        }

        private static void ThrowUnsupportedGrainType(GrainId grainId) => throw new InvalidOperationException($"Unsupported grain type for grain {grainId}");
    }

    internal static class AddressHelpers
    {
        public static ActivationAddress ToActivationAddress(this GrainAddress addr)
        {
            return ActivationAddress.GetAddress(
                    SiloAddress.FromParsableString(addr.SiloAddress),
                    GrainId.Parse(addr.GrainId),
                    ActivationId.GetActivationId(UniqueKey.Parse(addr.ActivationId.AsSpan())));
        }

        public static GrainAddress ToGrainAddress(this ActivationAddress addr)
        {
            return new GrainAddress
            {
                SiloAddress = addr.Silo.ToParsableString(),
                GrainId = addr.Grain.ToString(),
                ActivationId = (addr.Activation.Key.ToHexString())
            };
        }
    }
}
