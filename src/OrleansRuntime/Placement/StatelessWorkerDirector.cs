using System;
using System.Collections.Generic;
using System.Threading.Tasks;


namespace Orleans.Runtime.Placement
{
    internal class StatelessWorkerDirector : IPlacementDirector<StatelessWorkerPlacement>
    {
        private static readonly SafeRandom random = new SafeRandom();

        public Task<PlacementResult> OnSelectActivation(
            PlacementStrategy strategy, GrainId target, IPlacementContext context)
        {
            if (target.IsClient)
                throw new InvalidOperationException("Cannot use StatelessWorkerStrategy to route messages to client grains.");

            // If there are available (not busy with a request) activations, it returns the first one.
            // If all are busy and the number of local activations reached or exceeded MaxLocal, it randomly returns one of them.
            // Otherwise, it requests creation of a new activation.
            List<ActivationData> local;

            if (!context.LocalLookup(target, out local) || local.Count == 0)
                return Task.FromResult((PlacementResult)null);

            var placement = (StatelessWorkerPlacement)strategy;

            foreach (var activation in local)
            {
                ActivationData info;
                if (!context.TryGetActivationData(activation.ActivationId, out info) ||
                    info.State != ActivationState.Valid || !info.IsInactive) continue;

                return Task.FromResult(PlacementResult.IdentifySelection(ActivationAddress.GetAddress(context.LocalSilo, target, activation.ActivationId)));
            }

            if (local.Count >= placement.MaxLocal)
            {
                var id = local[local.Count == 1 ? 0 : random.Next(local.Count)].ActivationId;
                return Task.FromResult(PlacementResult.IdentifySelection(ActivationAddress.GetAddress(context.LocalSilo, target, id)));
            }

            return Task.FromResult((PlacementResult)null);
        }

        public Task<PlacementResult> OnAddActivation(PlacementStrategy strategy, GrainId grain, IPlacementContext context)
        {
            var grainType = context.GetGrainTypeName(grain);
            return Task.FromResult(
                PlacementResult.SpecifyCreation(context.LocalSilo, strategy, grainType));
        }

        internal static ActivationData PickRandom(List<ActivationData> local)
        {
             return local[local.Count == 1 ? 0 : random.Next(local.Count)];
        }
    }
}
