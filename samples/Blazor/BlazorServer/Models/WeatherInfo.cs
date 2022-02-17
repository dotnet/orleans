using Orleans.Concurrency;

namespace BlazorServer.Models;

[Immutable, Serializable]
public record class WeatherInfo(
    DateTime Date,
    int TemperatureC,
    string Summary,
    int TemperatureF);
