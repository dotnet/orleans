using Microsoft.AspNetCore.Mvc;
using Orleans;
using BlazorWasm.Grains;
using BlazorWasm.Models;
using System;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace Sample.Silo.Api
{
    [ApiController]
    [ApiVersion("1")]
    [Route("api/[controller]")]
    public class WeatherController : ControllerBase
    {
        private readonly IGrainFactory factory;

        public WeatherController(IGrainFactory factory)
        {
            this.factory = factory;
        }

        [HttpGet]
        public Task<ImmutableArray<WeatherInfo>> GetAsync() =>
            factory.GetGrain<IWeatherGrain>(Guid.Empty).GetForecastAsync();
    }
}