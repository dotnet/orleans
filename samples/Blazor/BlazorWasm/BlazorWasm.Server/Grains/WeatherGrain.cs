using Orleans;
using BlazorWasm.Models;
using System;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace BlazorWasm.Grains
{
    public class WeatherGrain : Grain, IWeatherGrain
    {
        private readonly ImmutableArray<WeatherInfo> data = ImmutableArray.Create(

            new WeatherInfo(DateTime.Today.AddDays(1), 1, "Freezing", 33),
            new WeatherInfo(DateTime.Today.AddDays(2), 14, "Bracing", 57),
            new WeatherInfo(DateTime.Today.AddDays(3), -13, "Freezing", 9),
            new WeatherInfo(DateTime.Today.AddDays(4), -16, "Balmy", 4),
            new WeatherInfo(DateTime.Today.AddDays(5), -2, "Chilly", 29));

        public Task<ImmutableArray<WeatherInfo>> GetForecastAsync() => Task.FromResult(data);
    }
}