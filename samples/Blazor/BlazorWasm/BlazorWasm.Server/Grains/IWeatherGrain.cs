using Orleans;
using BlazorWasm.Models;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace BlazorWasm.Grains
{
    public interface IWeatherGrain : IGrainWithGuidKey
    {
        Task<ImmutableArray<WeatherInfo>> GetForecastAsync();
    }
}