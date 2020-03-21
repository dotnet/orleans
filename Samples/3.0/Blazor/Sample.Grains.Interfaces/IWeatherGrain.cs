using Orleans;
using Sample.Grains.Models;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace Sample.Grains
{
    public interface IWeatherGrain : IGrainWithGuidKey
    {
        Task<ImmutableArray<WeatherInfo>> GetForecastAsync();
    }
}