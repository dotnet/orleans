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
        private readonly IGrainDirectoryResolver grainDirectoryResolver;
        private readonly DhtGrainLocator inClusterGrainLocator;
        private readonly IGrainDirectoryCache cache;

        private readonly CancellationTokenSource shutdownToken = new CancellationTokenSource();
        private readonly IClusterMembershipService clusterMembershipService;

        private Task listenToClusterChangeTask;

        internal interface ITestAccessor
        {
            MembershipVersion LastMembershipVersion { get; set; }
        }

        MembershipVersion ITestAccessor.LastMembershipVersion { get; set; }

        public CachedGrainLocator(
            IGrainDirectoryResolver grainDirectoryResolver,
            DhtGrainLocator inClusterGrainLocator,
            IClusterMembershipService clusterMembershipService)
        {
            this.grainDirectoryResolver = grainDirectoryResolver;
            this.inClusterGrainLocator = inClusterGrainLocator;
            this.clusterMembershipService = clusterMembershipService;
            this.cache = new LRUBasedGrainDirectoryCache(GrainDirectoryOptions.DEFAULT_CACHE_SIZE, GrainDirectoryOptions.DEFAULT_MAXIMUM_CACHE_TTL);
        }

        public async Task<List<ActivationAddress>> Lookup(GrainId grainId)
        {
            List<ActivationAddress> results;

            // Check cache first
            if (TryLocalLookup(grainId, out results))
            {
                return results;
            }

            results = new List<ActivationAddress>();

            var entry = await GetGrainDirectory(grainId).Lookup(grainId.ToParsableString());

            // Nothing found
            if (entry == null)
                return results;

            var activationAddress = entry.ToActivationAddress();

            // Check if the entry is pointing to a dead silo
            if (IsKnownDeadSilo(entry))
            {
                // Remove it from the directory
                await GetGrainDirectory(grainId).Unregister(entry);
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
            if (address.Grain.IsClient)
                return await this.inClusterGrainLocator.Register(address);

            var grainAddress = address.ToGrainAddress();
            grainAddress.MembershipVersion = (long) this.clusterMembershipService.CurrentSnapshot.Version;
            var grainId = address.Grain;

            var result = await GetGrainDirectory(grainId).Register(grainAddress);
            var activationAddress = result.ToActivationAddress();

            // Check if the entry point to a dead silo
            if (IsKnownDeadSilo(result))
            {
                // Remove outdated entry and retry to register
                await GetGrainDirectory(grainId).Unregister(result);
                result = await GetGrainDirectory(grainId).Register(grainAddress);
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
            if (this.cache.LookUp(grainId, out var results, out var version))
            {
                // IGrainDirectory only supports single activation
                var result = results[0];

                // If the silo is dead, remove the entry
                if (IsKnownDeadSilo(result.Item1, new MembershipVersion(version)))
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
                await GetGrainDirectory(address.Grain).Unregister(address.ToGrainAddress());
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

        private IGrainDirectory GetGrainDirectory(GrainId grainId) => this.grainDirectoryResolver.Resolve(grainId);

        private async Task ListenToClusterChange()
        {
            var previousSnapshot = this.clusterMembershipService.CurrentSnapshot;

            ((ITestAccessor)this).LastMembershipVersion = previousSnapshot.Version;

            var updates = this.clusterMembershipService.MembershipUpdates.WithCancellation(this.shutdownToken.Token);
            await foreach (var snapshot in updates)
            {
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

        private bool IsKnownDeadSilo(GrainAddress grainAddress)
            => IsKnownDeadSilo(SiloAddress.FromParsableString(grainAddress.SiloAddress), new MembershipVersion(grainAddress.MembershipVersion));

        private bool IsKnownDeadSilo(SiloAddress siloAddress, MembershipVersion membershipVersion)
        {
            var current = this.clusterMembershipService.CurrentSnapshot;

            // Check if the target silo is in the cluster
            if (current.Members.TryGetValue(siloAddress, out var value))
            {
                // It is, check if it's alive
                return value.Status.IsTerminating();
            }

            // We didn't find it in the cluster. If the silo entry is too old, it has been cleaned in the membership table: the entry isn't valid anymore.
            // Otherwise, maybe the membership service isn't up to date yet. The entry should be valid
            return current.Version > membershipVersion;
        }
    }

    internal static class AddressHelpers
    {
        public static ActivationAddress ToActivationAddress(this GrainAddress addr)
        {
            return ActivationAddress.GetAddress(
                    SiloAddress.FromParsableString(addr.SiloAddress),
                    GrainId.FromParsableString(addr.GrainId),
                    ActivationId.GetActivationId(UniqueKey.Parse(addr.ActivationId.AsSpan())));
        }

        public static GrainAddress ToGrainAddress(this ActivationAddress addr)
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
