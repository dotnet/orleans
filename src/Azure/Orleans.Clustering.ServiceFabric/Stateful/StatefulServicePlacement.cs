using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Clustering.ServiceFabric.Models;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Clustering.ServiceFabric
{
    /// <summary>
    /// Placement strategy for grains which are placed deterministically by mapping to a Service Fabric Stateful Service partition.
    /// </summary>
    public sealed class StatefulServicePlacement : PlacementStrategy
    {
        public static StatefulServicePlacement Instance { get; } = new StatefulServicePlacement();

        /// <inheritdoc />
        public override bool IsUsingGrainDirectory => false;

        /// <inheritdoc />
        public override bool IsDeterministicActivationId => true;
    }

    /// <summary>
    /// Specifies that this grain is placed by mapping to a Service Fabric Stateful Service partition.
    /// The activation will be placed on the primary for that partition and if the primary is not available, then placement calls will fail. 
    /// </summary>
    public class StatefulServicePlacementAttribute : PlacementAttribute
    {
        public StatefulServicePlacementAttribute() : base(StatefulServicePlacement.Instance)
        {
        }
    }

    /// <summary>
    /// Placement director for <see cref="StatefulServicePlacement"/>.
    /// </summary>
    internal class StatefulServicePlacementDirector : IPlacementDirector, IActivationSelector, IFabricServiceStatusListener
    {
        private readonly IFabricServiceSiloResolver resolver;

        private PartitionAddress[] partitions = Array.Empty<PartitionAddress>();

        public StatefulServicePlacementDirector(IFabricServiceSiloResolver resolver)
        {
            this.resolver = resolver;
            this.resolver.Subscribe(this);
        }

        /// <inheritdoc />
        public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            // This assumes that as long as we have information about one partition, we have information about all partitions.
            // Since partition count cannot change at runtime, this is considered to be a safe assumption.
            if (this.partitions.Length == 0)
            {
                return OnAddActivationAsync(strategy, target, context);
            }

            SiloAddress OnAddActivationSync(PlacementTarget t, IPlacementContext c)
            {
                var targetId = t.GrainIdentity.GetUniformHashCode();
                var result = this.partitions[targetId % this.partitions.Length];;
                if (result.SiloAddress == null)
                {
                    ThrowSiloUnavailableException(result);
                }

                return result.SiloAddress;
            }

            return Task.FromResult(OnAddActivationSync(target, context));

            async Task<SiloAddress> OnAddActivationAsync(PlacementStrategy s, PlacementTarget t, IPlacementContext p)
            {
                // Refresh triggers a call on OnUpdated.
                await this.resolver.Refresh();

                return OnAddActivationSync(t, p);
            }
        }

        /// <inheritdoc />
        public Task<PlacementResult> OnSelectActivation(PlacementStrategy strategy, GrainId target, IPlacementRuntime runtime)
        {
            if (this.partitions.Length == 0)
            {
                return Async(target, runtime);
            }

            return Task.FromResult(Sync(target, runtime));

            PlacementResult Sync(GrainId t, IPlacementRuntime r)
            {
                if (r.FastLookup(t, out var addresses))
                {
                    return PlacementResult.IdentifySelection(addresses.Addresses[0]);
                }

                var targetId = t.GetUniformHashCode();
                var partition = this.partitions[targetId % this.partitions.Length];
                var activationAddress = ActivationAddress.GetAddress(partition.SiloAddress, t, ActivationId.GetActivationId(t.Key));
                return PlacementResult.IdentifySelection(activationAddress);
            }

            async Task<PlacementResult> Async(GrainId t, IPlacementRuntime r)
            {
                // Refresh triggers a call on OnUpdated.
                await this.resolver.Refresh();

                return Sync(t, r);
            }
        }

        /// <inheritdoc />
        public bool TrySelectActivationSynchronously(
            PlacementStrategy strategy,
            GrainId target,
            IPlacementRuntime context,
            out PlacementResult placementResult)
        {
            if (context.FastLookup(target, out var addressesAndTag) && addressesAndTag.Addresses.Count > 0)
            {
                placementResult = PlacementResult.IdentifySelection(addressesAndTag.Addresses[0]);
                return true;
            }

            placementResult = null;
            return false;
        }

        /// <inheritdoc />
        public void OnUpdate(ServicePartitionSilos[] silos)
        {
            this.partitions = silos.Select(s =>
                {
                    var partitionId = s.Partition.Info.Id;
                    var primaryAddress = s.Silos.FirstOrDefault()?.SiloAddress;
                    return new PartitionAddress(partitionId, primaryAddress);
                })
                .OrderBy(s => s.PartitionId)
                .ToArray();
        }
        
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowSiloUnavailableException(PartitionAddress result)
        {
            throw new SiloUnavailableException($"Partition {result.PartitionId} is not ready to accept messages.");
        }

        private struct PartitionAddress
        {
            public PartitionAddress(Guid partitionId, SiloAddress siloAddress)
            {
                this.PartitionId = partitionId;
                this.SiloAddress = siloAddress;
            }

            public SiloAddress SiloAddress { get; }
            public Guid PartitionId { get; }
        }
    }
}