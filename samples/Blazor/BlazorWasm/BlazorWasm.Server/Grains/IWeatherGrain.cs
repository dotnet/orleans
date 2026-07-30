using BlazorWasm.Models;
using System.Collections.Immutable;

namespace BlazorWasm.Grains;

public interface IWeatherGrain : IGrainWithGuidKey
{
    Task<ImmutableArray<WeatherInfo>> GetForecastAsync();
}
