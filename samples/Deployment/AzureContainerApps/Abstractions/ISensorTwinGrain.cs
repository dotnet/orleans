using Orleans;
using System.Text.Json.Serialization;

namespace Abstractions
{
    public interface ISensorTwinGrain : IGrainWithStringKey
    {
        Task ReceiveSensorState(SensorState sensorState);
    }

    [Serializable]
    [GenerateSerializer]
    public class SensorState
    {
        [Id(0)]
        public string? SensorId { get; set; }

        [Id(1)]
        public double Value { get; set; }

        [Id(2)]
        public DateTime TimeStamp { get; set; } = DateTime.UtcNow;

        [Id(3)]
        public SensorType Type { get; set; } = SensorType.Unspecified;
    }

    [Serializable]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SensorType
    {
        Unspecified = 0,
        Motion = 1,
        Temperature = 2,
        Noise = 3,
        Breach = 4
    }
}
