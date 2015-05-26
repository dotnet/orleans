/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;


namespace Orleans.Runtime.Placement
{
    internal class StatelessWorkerDirector : PlacementDirector
    {
        private readonly SafeRandom random = new SafeRandom();

        internal override Task<PlacementResult> OnSelectActivation(
            PlacementStrategy strategy, GrainId target, IPlacementContext context)
        {
            if (target.IsClient)
                throw new InvalidOperationException("Cannot use StatelessWorkerStrategy to route messages to client grains.");

            // Returns an existing activation of the grain or requests creation of a new one.
            // If there are available (not busy with a request) activations, it returns the first one
            // that exceeds the MinAvailable limit. E.g. if MinAvailable=1, it returns the second available.
            // If MinAvailable is less than 1, it returns the first available.
            // If the number of local activations reached or exceeded MaxLocal, it randomly returns one of them.
            // Otherwise, it requests creation of a new activation.
            List<ActivationData> local;

            if (!context.LocalLookup(target, out local) || local.Count == 0)
                return Task.FromResult((PlacementResult)null);

            var placement = (StatelessWorkerPlacement)strategy;
            List<ActivationId> available = null;

            foreach (var activation in local)
            {
                ActivationData info;
                if (!context.TryGetActivationData(activation.ActivationId, out info) ||
                    info.State != ActivationState.Valid || !info.IsInactive) continue;

                if (placement.MinAvailable < 1 || (available != null && available.Count >= placement.MinAvailable))
                    return Task.FromResult(PlacementResult.IdentifySelection(ActivationAddress.GetAddress(context.LocalSilo, target, activation.ActivationId)));

                if (available == null)
                    available = new List<ActivationId>();
                available.Add(info.ActivationId);
            }

            if (local.Count >= placement.MaxLocal)
            {
                var id = local[local.Count == 1 ? 0 : random.Next(local.Count)].ActivationId;
                return Task.FromResult(PlacementResult.IdentifySelection(ActivationAddress.GetAddress(context.LocalSilo, target, id)));
            }

            return Task.FromResult((PlacementResult)null);
        }

        internal override Task<PlacementResult> OnAddActivation(PlacementStrategy strategy, GrainId grain, IPlacementContext context)
        {
            var grainType = context.GetGrainTypeName(grain);
            return Task.FromResult(
                PlacementResult.SpecifyCreation(context.LocalSilo, strategy, grainType));
        }
    }
}
