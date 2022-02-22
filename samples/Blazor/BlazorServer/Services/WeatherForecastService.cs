using BlazorServer.Models;
using Orleans;
using System.Collections.Immutable;

namespace BlazorServer.Services;

public class WeatherForecastService
{
    private readonly IClusterClient _client;

    public WeatherForecastService(IClusterClient client)
    {
        _client = client;
    }

    public Task<ImmutableArray<WeatherInfo>> GetForecastAsync() =>
        _client.GetGrain<IWeatherGrain>(Guid.Empty).GetForecastAsync();
}
