using BlazorServer.Models;
using Orleans;
using System.Collections.Immutable;

namespace BlazorServer;

public interface IWeatherGrain : IGrainWithGuidKey
{
    Task<ImmutableArray<WeatherInfo>> GetForecastAsync();
}
