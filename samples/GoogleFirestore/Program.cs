using System.Net;
using FirestoreSample;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;

var builder = Host.CreateApplicationBuilder(args);
var projectId = builder.Configuration["GoogleCloud:ProjectId"]
    ?? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
var emulatorHost = builder.Configuration["GoogleCloud:EmulatorHost"]
    ?? Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");
var rootCollectionName = builder.Configuration["GoogleCloud:RootCollectionName"]
    ?? "OrleansSample";

if (string.IsNullOrWhiteSpace(projectId))
{
    Console.Error.WriteLine(
        "Set GOOGLE_CLOUD_PROJECT or GoogleCloud:ProjectId to a Google Cloud project or emulator project ID.");
    return 1;
}

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "google-firestore-sample";
        options.ServiceId = "google-firestore-sample";
    });

    siloBuilder.Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback);

    siloBuilder.UseFirestoreClustering(options =>
    {
        options.ProjectId = projectId;
        options.RootCollectionName = rootCollectionName;
        options.EmulatorHost = emulatorHost;
    });

    siloBuilder.UseFirestoreGrainDirectoryAsDefault(options =>
    {
        options.ProjectId = projectId;
        options.RootCollectionName = rootCollectionName;
        options.EmulatorHost = emulatorHost;
    });

    siloBuilder.AddFirestoreGrainStorage("firestore", options =>
    {
        options.ProjectId = projectId;
        options.RootCollectionName = rootCollectionName;
        options.EmulatorHost = emulatorHost;
    });

    siloBuilder.UseFirestoreReminderService(options =>
    {
        options.ProjectId = projectId;
        options.RootCollectionName = rootCollectionName;
        options.EmulatorHost = emulatorHost;
    });
});

using var host = builder.Build();
await host.StartAsync();

var client = host.Services.GetRequiredService<IClusterClient>();
var counter = client.GetGrain<ICounterGrain>("sample");
var value = await counter.Increment();
await counter.EnsureReminder();

Console.WriteLine($"Persistent counter value: {value}");
Console.WriteLine("A durable reminder named 'heartbeat' is registered.");
Console.WriteLine("Run the sample again to observe the persisted counter increment.");

await host.StopAsync();
return 0;
