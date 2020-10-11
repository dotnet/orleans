using Orleans;
using Sample.Grains;
using Sample.Grains.Models;
using System;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace Sample.ServerSide.Services
{
    public class WeatherForecastService
    {
        private readonly IClusterClient client;

        public WeatherForecastService(IClusterClient client)
        {
            this.client = client;
        }

        public Task<ImmutableArray<WeatherInfo>> GetForecastAsync() =>

            client.GetGrain<IWeatherGrain>(Guid.Empty)
                .GetForecastAsync();
    }
}