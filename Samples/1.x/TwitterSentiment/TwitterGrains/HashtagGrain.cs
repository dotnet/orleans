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
