using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using Orleans;
using Orleans.Providers;
using GrainInterfaces;

namespace GrainCollection
{

    [StorageProvider(ProviderName = "Default")]
    public class CollectorGrain : Grain<CollectorState>, ICollector
    {
        private CollectorState state = new CollectorState();

        //This is a workaround required because the Grainfactory property is not virtual
        public new virtual IGrainFactory GrainFactory
        {
            get
            {

                return base.GrainFactory;
            }
        }

        
        public async Task<long> GetSum()
        {
            var streamProvider = GetStreamProvider("SMS");
            var resultStream = streamProvider.GetStream<long>(Guid.NewGuid(), "results"); 

            List<Task> pendingTasks = new List<Task>();
            for(int i = 0; i < 10; i++)
            {
                var workerGrain = GrainFactory.GetGrain<IWorker>(i);
                pendingTasks.Add(await Task.Factory.StartNew(async delegate
                {
                    var result = await workerGrain.GetAnswer();

                    //Let's stream a result here
                    await resultStream.OnNextAsync(result);

                    state.sum += result; //This wouldn't work properly if the scheduler was multithreaded
                }));

            }

            await Task.WhenAll(pendingTasks);
            //Let's persist here
            await WriteStateAsync();

            return state.sum;
        }
    }

    public class CollectorState
    {
        public long sum;
    }
}
