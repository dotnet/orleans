using System;
using System.Threading.Tasks;
using FasterSample.Grains;
using Orleans;

namespace FasterSample.Grains
{
    public interface IDictionaryFrequencyGrain : IGrainWithIntegerKey
    {
        Task<FrequencyItem> GetValueByKeyAsync(int key);

        Task AccumulateAsync(int key, FrequencyItem value);
    }
}

namespace Orleans
{
    public static class DictionaryFrequencyGrainFactoryExtensions
    {
        public static IDictionaryFrequencyGrain GetDictionaryFrequencyGrain(this IGrainFactory factory, int key)
        {
            if (factory is null) throw new ArgumentNullException(nameof(factory));

            return factory.GetGrain<IDictionaryFrequencyGrain>(key);
        }
    }
}