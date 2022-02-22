using System.Collections.Immutable;
using BlazorServer.Models;
using Orleans;

namespace BlazorServer;

public class WeatherGrain : Grain, IWeatherGrain
{
    private readonly ImmutableArray<WeatherInfo> _data = ImmutableArray.Create<WeatherInfo>(

        new(DateTime.Today.AddDays(1), 1, "Freezing", 33),
        new(DateTime.Today.AddDays(2), 14, "Bracing", 57),
        new(DateTime.Today.AddDays(3), -13, "Freezing", 9),
        new(DateTime.Today.AddDays(4), -16, "Balmy", 4),
        new(DateTime.Today.AddDays(5), -2, "Chilly", 29));

    public Task<ImmutableArray<WeatherInfo>> GetForecastAsync() => Task.FromResult(_data);
}
