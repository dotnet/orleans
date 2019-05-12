using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Grains.Models;
using Orleans;
using Orleans.Concurrency;

namespace Grains
{
    [Reentrant]
    public class ConcurrentDictionaryGrain : Grain, IConcurrentDictionaryGrain
    {
        private readonly ConcurrentDictionary<int, LookupItem> lookup = new ConcurrentDictionary<int, LookupItem>();

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
            return Task.Run(() =>
            {
                foreach (var item in items)
                {
                    lookup[item.Key] = item;
                }
            });
        }

        public Task StartAsync() => Task.CompletedTask;

        public Task StopAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public async Task<ImmutableList<LookupItem>> TryGetRangeAsync(ImmutableList<int> keys)
        {
            var results = ImmutableList.CreateBuilder<LookupItem>();
            await Task.Run(() =>
            {
                foreach (var key in keys)
                {
                    if (lookup.TryGetValue(key, out var value))
                    {
                        results.Add(value);
                    }
                }
            });
            return results.ToImmutable();
        }

        public Task SetRangeDeltaAsync(ImmutableList<LookupItem> items)
        {
            return Task.Run(() =>
            {
                foreach (var item in items)
                {
                    lookup.AddOrUpdate(item.Key, item, (key, existing) => new LookupItem(key, existing.Value + item.Value, item.Timestamp));
                }
            });
        }
    }
}