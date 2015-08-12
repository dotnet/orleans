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
