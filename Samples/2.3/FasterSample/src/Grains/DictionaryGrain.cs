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

        public Task<ImmutableList<LookupItem>> TryGetRangeAsync(ImmutableList<int> keys)
        {
            var results = ImmutableList.CreateBuilder<LookupItem>();
            foreach (var key in keys)
            {
                if (lookup.TryGetValue(key, out var value))
                {
                    results.Add(value);
                }
            }
            return Task.FromResult(results.ToImmutable());
        }

        public Task SetRangeDeltaAsync(ImmutableList<LookupItem> items)
        {
            foreach (var item in items)
            {
                if (lookup.TryGetValue(item.Key, out var existing))
                {
                    lookup[item.Key] = new LookupItem(item.Key, existing.Value + item.Value, item.Timestamp);
                }
                else
                {
                    lookup[item.Key] = item;
                }
            }
            return Task.CompletedTask;
        }
    }
}