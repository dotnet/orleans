using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        public async ValueTask<ActivationAddress> Lookup(GrainId grainId)
        {
            var grainType = grainId.Type;
            if (grainType.IsClient() || grainType.IsSystemTarget())
            {
                ThrowUnsupportedGrainType(grainId);
            }

            // Check cache first
            if (TryLocalLookup(grainId, out var cachedResult))
            {
                return cachedResult;
            }

            var entry = await GetGrainDirectory(grainId.Type).Lookup(grainId);

            // Nothing found
            if (entry is null)
            {
                return null;
            }

            var entryAddress = entry.ToActivationAddress();
            ActivationAddress result;

            // Check if the entry is pointing to a dead silo
            if (IsKnownDeadSilo(entry))
            {
                // Remove it from the directory
                await GetGrainDirectory(grainId.Type).Unregister(entry);
                result = null;
            }
            else
            {
                // Add to the local cache and return it
                this.cache.AddOrUpdate(entryAddress, 0);
                result = entryAddress;
            }

            return result;
        }

        public async Task<ActivationAddress> Register(ActivationAddress address)
        {
            var grainType = address.Grain.Type;
            if (grainType.IsClient() || grainType.IsSystemTarget())
            {
                ThrowUnsupportedGrainType(address.Grain);
            }

            var grainAddress = address.ToGrainAddress();
            grainAddress.MembershipVersion = this.clusterMembershipService.CurrentSnapshot.Version;

            var result = await GetGrainDirectory(grainType).Register(grainAddress);
            var activationAddress = result.ToActivationAddress();

            // Check if the entry point to a dead silo
            if (IsKnownDeadSilo(result))
            {
                // Remove outdated entry and retry to register
                await GetGrainDirectory(grainType).Unregister(result);
                result = await GetGrainDirectory(grainType).Register(grainAddress);
                activationAddress = result.ToActivationAddress();
            }

            // Cache update
            this.cache.AddOrUpdate(activationAddress, (int) result.MembershipVersion.Value);

            return activationAddress;
        }

        public bool TryLocalLookup(GrainId grainId, out ActivationAddress result)
        {
            var grainType = grainId.Type;
            if (grainType.IsClient() || grainType.IsSystemTarget())
            {
                ThrowUnsupportedGrainType(grainId);
            }

            if (this.cache.LookUp(grainId, out result, out var version))
            {
                // If the silo is dead, remove the entry
                if (IsKnownDeadSilo(result.Silo, new MembershipVersion(version)))
                {
                    result = default;
                    this.cache.Remove(grainId);
                }
                else
                {
                    // Entry found and valid -> return it
                    return true;
                }
            }

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

            ((ITestAccessor)this).LastMembershipVersion = previousSnapshot.Version;

            var updates = this.clusterMembershipService.MembershipUpdates.WithCancellation(this.shutdownToken.Token);
            await foreach (var snapshot in updates)
            {
                // Active filtering: detect silos that went down and try to clean proactively the directory
                var changes = snapshot.CreateUpdate(previousSnapshot).Changes;
                var deadSilos = changes
                    .Where(member => member.Status.IsTerminating())
                    .Select(member => member.SiloAddress)
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
            => IsKnownDeadSilo(grainAddress.SiloAddress, grainAddress.MembershipVersion);

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

        private static void ThrowUnsupportedGrainType(GrainId grainId) => throw new InvalidOperationException($"Unsupported grain type for grain {grainId}");
    }

    internal static class AddressHelpers
    {
        public static ActivationAddress ToActivationAddress(this GrainAddress addr)
        {
            return ActivationAddress.GetAddress(
                    addr.SiloAddress,
                    addr.GrainId,
                    ActivationId.GetActivationId(UniqueKey.Parse(addr.ActivationId.AsSpan())));
        }

        public static GrainAddress ToGrainAddress(this ActivationAddress addr)
        {
            return new GrainAddress
            {
                SiloAddress = addr.Silo,
                GrainId = addr.Grain,
                ActivationId = (addr.Activation.Key.ToHexString())
            };
        }
    }
}
