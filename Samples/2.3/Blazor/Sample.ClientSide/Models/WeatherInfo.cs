﻿using System;

namespace Sample.ClientSide.Models
{
    public class WeatherInfo
    {
        public DateTime Date { get; set; }
        public int TemperatureC { get; set; }
        public string Summary { get; set; }
        public int TemperatureF { get; set; }
    }
}