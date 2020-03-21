using System.Threading.Tasks;
using GrainInterfaces;
using Orleans.Concurrency;
using Orleans;

namespace GrainCollection
{
    
    class WorkerGrain : Orleans.Grain, IWorker
    {
        public async Task<long> GetAnswer()
        {
            //This might go off and do something that we don't want to repeat in testing
            await Task.Delay(100); //simulate some network io
            return this.GetPrimaryKeyLong();
        }
    }
}
