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

        public Task<LookupItem> TryGetAsync(int key) => Task.Run(() =>
        {
            lookup.TryGetValue(key, out var item);
            return Task.FromResult(item);
        });

        public Task SetAsync(LookupItem item) => Task.Run(() =>
        {
            lookup[item.Key] = item;
            return Task.CompletedTask;
        });

        public Task SetRangeAsync(ImmutableList<LookupItem> items) => Task.Run(() =>
        {
            foreach (var item in items)
            {
                lookup[item.Key] = item;
            }
            return Task.CompletedTask;
        });

        public Task StartAsync() => Task.CompletedTask;

        public Task StopAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }
}