using BlazorServer.Models;
using Orleans;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace BlazorServer
{
    public interface IWeatherGrain : IGrainWithGuidKey
    {
        Task<ImmutableArray<WeatherInfo>> GetForecastAsync();
    }
}