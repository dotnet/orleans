using GPSTracker.Common;
using GPSTracker.GrainInterface;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;

namespace GPSTracker.FakeDeviceGateway;

internal static class Program
{
    private static Random Random => Random.Shared;

    // San Francisco: approximate boundaries.
    private const double SFLatMin = 37.708;
    private const double SFLatMax = 37.78;
    private const double SFLonMin = -122.50;
    private const double SFLonMax = -122.39;

    private static int _counter = 0;

    private static async Task Main(string[] args)
    {
        var host = new HostBuilder()
            .UseOrleansClient(builder => builder.UseLocalhostClustering())
            .UseConsoleLifetime()
            .Build();

        await host.StartAsync();

        // Simulate 20 devices
        var devices = new List<Model>();
        for (var i = 0; i < 25; i++)
        {
            devices.Add(new Model
            {
                DeviceId = i,
                Lat = NextDouble(SFLatMin, SFLatMax),
                Lon = NextDouble(SFLonMin, SFLonMax),
                Direction = NextDouble(-Math.PI, Math.PI),
                Speed = NextDouble(0, 0.0005)
            });
        }

        await using Timer timer = new(_ =>
        {
            Console.Write(". ");
            Interlocked.Exchange(ref _counter, 0);
        }, null, 1_000, 1_000);

        IGrainFactory factory = host.Services.GetRequiredService<IGrainFactory>();
        IHostApplicationLifetime lifeTime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        // Update each device in a loop.
        var tasks = new List<Task>(capacity: devices.Count);
        while (!lifeTime.ApplicationStopping.IsCancellationRequested)
        {
            foreach (var model in devices)
            {
                tasks.Add(SendMessage(factory, model));
            }

            await Task.WhenAll(tasks);
            tasks.Clear();
        }

        Console.WriteLine("Received Ctrl-C. Stopping");
    }

    private static async Task SendMessage(IGrainFactory grainFactory, Model model)
    {
        try
        {
            // There is nothing particular about these values, they are just simulating a random walk.
            var delta = model.TimeSinceLastUpdate.Elapsed.TotalMilliseconds;

            // Simulate the device moving
            model.Acceleration = Math.Clamp(model.Acceleration + delta / 100 * NextDouble(-0.0005, 0.0005), -10, 10);
            model.Speed = Math.Clamp(model.Speed + model.Acceleration * delta / 100, -0.0005, 0.0005);

            model.AngularVelocity = Math.Clamp(model.AngularVelocity + delta * NextDouble(-0.005, 0.005), -0.01, 0.01);
            model.Direction += model.AngularVelocity * delta;

            var lastLat = model.Lat;
            var lastLon = model.Lon;

            UpdateDevicePosition(model, delta);

            if (lastLat == model.Lat || lastLon == model.Lon)
            {
                // The device has hit the boundary, so change direction.
                model.Direction += NextDouble(-Math.PI, Math.PI);

                UpdateDevicePosition(model, delta);
            }

            model.TimeSinceLastUpdate.Restart();

            // Send the mesage to the service
            var device = grainFactory.GetGrain<IDeviceGrain>(model.DeviceId);

            await device.ProcessMessage(
                new DeviceMessage(
                    model.Lat,
                    model.Lon,
                    ++model.MessageId,
                    model.DeviceId,
                    DateTime.UtcNow));

            Interlocked.Increment(ref _counter);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Exception sending message: {exception}");
        }
    }

    private static void UpdateDevicePosition(Model model, double delta)
    {
        model.Lat += Math.Cos(model.Direction) * (model.Speed * delta / 10);
        model.Lon += Math.Sin(model.Direction) * (model.Speed * delta / 10);
        model.Lat = Math.Clamp(model.Lat, SFLatMin, SFLatMax);
        model.Lon = Math.Clamp(model.Lon, SFLonMin, SFLonMax);
    }

    public static double NextDouble(double min, double max) => Random.NextDouble() * (max - min) + min;

    private sealed class Model
    {
        public Stopwatch TimeSinceLastUpdate { get; } = Stopwatch.StartNew();
        public int DeviceId { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double Direction { get; set; }
        public double AngularVelocity { get; set; }
        public double Acceleration { get; set; }
        public double Speed { get; set; }
        public long MessageId { get; set; }
    }
}
