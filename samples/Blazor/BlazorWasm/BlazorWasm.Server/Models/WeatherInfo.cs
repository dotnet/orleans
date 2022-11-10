namespace BlazorWasm.Models;

[Immutable]
[GenerateSerializer]
public record class WeatherInfo(
    DateTime Date,
    int TemperatureC,
    string Summary,
    int TemperatureF);