using Abstractions;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;

namespace Grains
{
    [CollectionAgeLimit(Minutes = 2)]
    [DontPlaceMeOnTheDashboard]
    public class SensorTwinGrain : Grain, ISensorTwinGrain
    {
        public ILogger<SensorTwinGrain> Logger { get; set; }

        public SensorTwinGrain(ILogger<SensorTwinGrain> logger) => Logger = logger;

        public async Task ReceiveSensorState(SensorState sensorState) =>
            await Task.Run(() => Logger.LogInformation($"Received value of {sensorState.Value} for {sensorState.Type} state reading from sensor {this.GetPrimaryKeyString()}"));

    }
}
