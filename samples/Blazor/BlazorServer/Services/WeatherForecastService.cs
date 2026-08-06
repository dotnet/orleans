using BlazorServer.Models;
using System.Collections.Immutable;

namespace BlazorServer.Services;

public sealed class WeatherForecastService(IClusterClient client)
{
    public Task<ImmutableArray<WeatherInfo>> GetForecastAsync() =>
        client.GetGrain<IWeatherGrain>(Guid.Empty).GetForecastAsync();
}
