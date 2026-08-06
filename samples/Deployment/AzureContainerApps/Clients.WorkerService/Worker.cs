using Abstractions;
using Orleans;

namespace Clients.WorkerService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IClusterClient _orleansClusterClient;

        public Worker(ILogger<Worker> logger, IClusterClient orleansClusterClient)
        {
            _logger = logger;
            _orleansClusterClient = orleansClusterClient;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var randomDeviceIDs = new List<string>();
            var randomDevices = new Dictionary<string, ISensorTwinGrain>();

            for (int i = 0; i < 256; i++)
            {
                var key = $"device{i.ToString().PadLeft(5, '0')}-{Random.Shared.Next(10000, 99999)}-{Environment.MachineName}";
                randomDeviceIDs.Add(key);
                randomDevices.Add(key, _orleansClusterClient.GetGrain<ISensorTwinGrain>(key));
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Parallel.ForEachAsync(randomDeviceIDs, stoppingToken, async (deviceId, _) =>
                {
                    await randomDevices[deviceId].ReceiveSensorState(new SensorState
                    {
                        SensorId = deviceId,
                        TimeStamp = DateTime.UtcNow,
                        Type = SensorType.Unspecified,
                        Value = Random.Shared.Next(0, 100)
                    });
                });
            }
        }
    }
}