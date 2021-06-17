using Orleans.Concurrency;
using System;

namespace BlazorServer.Models
{
    [Immutable]
    [Serializable]
    public class WeatherInfo
    {
        public WeatherInfo(DateTime date, int temperatureC, string summary, int temperatureF)
        {
            Date = date;
            TemperatureC = temperatureC;
            Summary = summary;
            TemperatureF = temperatureF;
        }

        public DateTime Date { get; }
        public int TemperatureC { get; }
        public string Summary { get; }
        public int TemperatureF { get; }
    }
}