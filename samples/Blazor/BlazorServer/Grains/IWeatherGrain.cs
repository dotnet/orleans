using BlazorServer.Models;
using System.Collections.Immutable;

namespace BlazorServer;

public interface IWeatherGrain : IGrainWithGuidKey
{
    Task<ImmutableArray<WeatherInfo>> GetForecastAsync();
}
