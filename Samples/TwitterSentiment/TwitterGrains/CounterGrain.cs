using System.Threading.Tasks;
using TwitterGrainInterfaces;

using Orleans;
using Orleans.Providers;
using Orleans.Concurrency;

namespace TwitterGrains
{
    /// <summary>
    /// interface defining the persistent state for the counter grain
    /// </summary>
    public class CounterState : GrainState
    {
        /// <summary>
        /// total number of hashtag grain activations
        /// </summary>
        public int Counter { get; set; }
    }

    [StorageProvider(ProviderName = "store1")]
    [Reentrant]
    public class CounterGrain : Grain<CounterState>, ICounter
    {
        /// <summary>
        /// Add one to the activation count
        /// </summary>
        /// <returns></returns>
        public async Task IncrementCounter()
        {
            this.State.Counter += 1;

            // as an optimisation, only write out the state for every 100 increments 
            if (this.State.Counter % 100 == 0) await WriteStateAsync();
        }

        /// <summary>
        /// Reset the counter to zero
        /// </summary>
        /// <returns></returns>
        public async Task ResetCounter()
        {
            this.State.Counter = 0;
            await this.WriteStateAsync();
        }

        /// <summary>
        /// Retrieve the total count
        /// </summary>
        /// <returns></returns>
        public Task<int> GetTotalCounter()
        {
            return Task.FromResult(this.State.Counter);
        }

    }
}
