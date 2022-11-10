using Microsoft.AspNetCore.Mvc;
using BlazorWasm.Grains;
using BlazorWasm.Models;

namespace Sample.Silo.Api;

[ApiController]
[ApiVersion("1")]
[Route("api/[controller]")]
public class WeatherController : ControllerBase
{
    private readonly IGrainFactory _factory;

    public WeatherController(IGrainFactory factory) => _factory = factory;

    [HttpGet]
    public async Task<IEnumerable<WeatherInfo>> GetAsync() =>
        await _factory.GetGrain<IWeatherGrain>(Guid.Empty).GetForecastAsync();
}
