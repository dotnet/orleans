using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Grains.Models;
using Orleans;
using Orleans.Concurrency;

namespace Grains
{
    [Reentrant]
    public class DictionaryGrain : Grain, IDictionaryGrain
    {
        private readonly Dictionary<int, LookupItem> lookup = new Dictionary<int, LookupItem>();

        public Task<LookupItem> TryGetAsync(int key)
        {
            lookup.TryGetValue(key, out var item);
            return Task.FromResult(item);
        }

        public Task SetAsync(LookupItem item)
        {
            lookup[item.Key] = item;
            return Task.CompletedTask;
        }

        public Task SetRangeAsync(ImmutableList<LookupItem> items)
        {
            foreach (var item in items)
            {
                lookup[item.Key] = item;
            }
            return Task.CompletedTask;
        }

        public Task StartAsync() => Task.CompletedTask;

        public Task StopAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }
}