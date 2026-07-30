namespace BlazorWasm.Models;

public record class WeatherInfo
{
    public DateTime Date { get; set; }
    public int TemperatureC { get; set; }
    public string Summary { get; set; } = null!;
    public int TemperatureF { get; set; }
}
