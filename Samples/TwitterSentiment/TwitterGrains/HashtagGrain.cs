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
using System.Threading.Tasks;
using TwitterGrainInterfaces;

using Orleans;
using Orleans.Providers;

namespace TwitterGrains
{
    /// <summary>
    /// interface defining the persistent state for hashtag grain
    /// </summary>
    public class TotalsState : GrainState
    {
        public int Positive { get; set; }
        public int Negative { get; set; }
        public int Total { get; set; }
        public DateTime LastUpdated { get; set; }
        public string Hashtag { get; set; }
        public bool BeenCounted { get; set; }
        public string LastTweet { get; set; }
    }


    // <Provider Type="Orleans.Storage.AzureTableStorage" Name="store1" DataConnectionString="xxx" />
    [StorageProvider(ProviderName = "store1")]
    public class HashtagGrain : Grain<TotalsState>, IHashtagGrain
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
                var counter = GrainFactory.GetGrain<ICounter>(0);
                await Task.WhenAll(counter.IncrementCounter(), this.WriteStateAsync());
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
                await this.WriteStateAsync();
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
