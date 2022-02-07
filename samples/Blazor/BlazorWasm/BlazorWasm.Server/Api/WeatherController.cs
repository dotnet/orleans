using Microsoft.AspNetCore.Mvc;
using Orleans;
using BlazorWasm.Grains;
using BlazorWasm.Models;
using System.Collections.Immutable;

namespace Sample.Silo.Api;

[ApiController]
[ApiVersion("1")]
[Route("api/[controller]")]
public class WeatherController : ControllerBase
{
    private readonly IGrainFactory _factory;

    public WeatherController(IGrainFactory factory) => _factory = factory;

    [HttpGet]
    public Task<ImmutableArray<WeatherInfo>> GetAsync() =>
        _factory.GetGrain<IWeatherGrain>(Guid.Empty).GetForecastAsync();
}
