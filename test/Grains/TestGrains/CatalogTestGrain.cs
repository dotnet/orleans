using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class CatalogTestGrain : Grain, ICatalogTestGrain
    {
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        public Task Initialize()
        {
            return Task.CompletedTask;
        }

        public Task BlastCallNewGrains(int nGrains, long startingKey, int nCallsToEach)
        {
            var promises = new List<Task>(nGrains * nCallsToEach);

            for (int i = 0; i < nGrains; i++)
            {
                var grain = GrainFactory.GetGrain<ICatalogTestGrain>(startingKey + i);

                for (int j = 0; j < nCallsToEach; j++)
                    promises.Add(grain.Initialize());
            }

            return Task.WhenAll(promises);
        }
    }
}
