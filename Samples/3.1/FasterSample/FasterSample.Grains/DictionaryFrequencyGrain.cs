using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace FasterSample.Grains
{
    public class DictionaryFrequencyGrain : Grain, IDictionaryFrequencyGrain
    {
        private readonly Dictionary<int, FrequencyItem> _data = new Dictionary<int, FrequencyItem>();

        public Task AccumulateAsync(int key, FrequencyItem value)
        {
            _data[key] = _data.TryGetValue(key, out var current) ? current.Add(value) : value;

            return Task.CompletedTask;
        }

        public Task<FrequencyItem> GetValueByKeyAsync(int key) => Task.FromResult(_data.TryGetValue(key, out var value) ? value : null);
    }
}