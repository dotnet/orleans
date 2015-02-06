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

using System;
using System.Threading.Tasks;
using TwitterGrainInterfaces;

using Orleans;
using Orleans.Providers;

namespace TwitterGrains
{
    /// <summary>
    /// interface defining the persistent state for hashtag grain
    /// </summary>
    public interface ITotalsState : IGrainState
    {
        int Positive { get; set; }
        int Negative { get; set; }
        int Total { get; set; }
        DateTime LastUpdated { get; set; }
        string Hashtag { get; set; }
        bool BeenCounted { get; set; }
        string LastTweet { get; set; }
    }


    // <Provider Type="Orleans.Storage.AzureTableStorage" Name="store1" DataConnectionString="xxx" />
    [StorageProvider(ProviderName = "store1")]
    public class HashtagGrain : Orleans.Grain<ITotalsState>, IHashtagGrain
    {
        private string hashtag;  // keep note of the hashtag we are tracking

        public override async Task OnActivateAsync()
        {
            this.GetPrimaryKey(out hashtag);
            this.State.Hashtag = hashtag;

            // if this is our first ever activation, let the Counter Grain know
            if (!this.State.BeenCounted)
            {
                // record that the grain has now been counted, and store the state
                this.State.BeenCounted = true;
                var counter = CounterFactory.GetGrain(0);
                await Task.WhenAll(counter.IncrementCounter(), this.State.WriteStateAsync());
            }
            await base.OnActivateAsync();
        }

        public async Task AddScore(int score, string lastTweet)
        {
            this.State.LastUpdated = DateTime.UtcNow;
            this.State.LastTweet = lastTweet;
            this.State.Total += 1;

            // update sentiment score
            if (score > 0)
            {
                this.State.Positive += 1;
            }

            if (score < 0)
            {
                this.State.Negative += 1;
            }

            if (score != 0)
            {
                // only save the state if the score is non-zero (otherwise it's not interesting)
                await this.State.WriteStateAsync();
            }
        }

        public Task<Totals> GetTotals()
        {
            return Task.FromResult(new Totals
            {
                Total = this.State.Total,
                Positive = this.State.Positive,
                Negative = this.State.Negative,
                Hashtag = this.State.Hashtag,
                LastUpdated = this.State.LastUpdated,
                LastTweet = this.State.LastTweet
            });
        }

        public override Task OnDeactivateAsync()
        {
            return base.OnDeactivateAsync();
        }
    }
}
