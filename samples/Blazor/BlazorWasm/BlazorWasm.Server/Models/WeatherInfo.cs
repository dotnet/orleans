using Orleans.Concurrency;

namespace BlazorWasm.Models;

[Immutable]
public record class WeatherInfo(
    DateTime Date,
    int TemperatureC,
    string Summary,
    int TemperatureF);