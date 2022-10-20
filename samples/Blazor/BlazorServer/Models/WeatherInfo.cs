using Orleans;
using Orleans.Concurrency;

namespace BlazorServer.Models;

[Immutable, Serializable]
[GenerateSerializer]
public record class WeatherInfo(
    DateTime Date,
    int TemperatureC,
    string Summary,
    int TemperatureF);
