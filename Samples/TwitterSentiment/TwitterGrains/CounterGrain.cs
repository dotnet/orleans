//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************

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
    public interface ICounterState : IGrainState
    {
        /// <summary>
        /// total number of hashtag grain activations
        /// </summary>
        int Counter { get; set; }
    }

    [StorageProvider(ProviderName = "store1")]
    [Reentrant]
    public class CounterGrain : Orleans.Grain<ICounterState>, ICounter
    {
        /// <summary>
        /// Add one to the activation count
        /// </summary>
        /// <returns></returns>
        public async Task IncrementCounter()
        {
            this.State.Counter += 1;

            // as an optimisation, only write out the state for every 100 increments 
            if (this.State.Counter % 100 == 0) await State.WriteStateAsync();
        }

        /// <summary>
        /// Reset the counter to zero
        /// </summary>
        /// <returns></returns>
        public async Task ResetCounter()
        {
            this.State.Counter = 0;
            await this.State.WriteStateAsync();
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
