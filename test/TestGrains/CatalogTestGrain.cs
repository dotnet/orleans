using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class CatalogTestGrain : Grain, ICatalogTestGrain
    {
        public override Task OnActivateAsync()
        {
            return Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        public Task Initialize()
        {
            return TaskDone.Done;
        }

        public async Task BlastCallNewGrains(int nGrains, long startingKey, int nCallsToEach)
        {
            var promises = new List<Task>();

            for (int i = 0; i < nGrains; i++)
            {
                var grain = GrainFactory.GetGrain<ICatalogTestGrain>((startingKey + i).ToString(CultureInfo.InvariantCulture));

                for(int j=0; j<nCallsToEach; j++)
                    promises.Add(grain.Initialize());
            }

            await Task.WhenAll(promises);
        }
    }
}