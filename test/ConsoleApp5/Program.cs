using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Journaling;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(builder =>
    {
        builder.AddConsole().SetMinimumLevel(LogLevel.Error);
        builder.AddFilter("Orleans.Journaling.StateMachineManager", LogLevel.Debug);
    })
    .UseOrleans(builder =>
    {
        builder.UseLocalhostClustering();
#pragma warning disable ORLEANSEXP005 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        builder.Configure<StateMachineManagerOptions>(o => o.RetirementGracePeriod = TimeSpan.FromSeconds(10));
        builder.AddAzureAppendBlobStateMachineStorage(options =>
        {
            options.ContainerName = "test-grains";
            options.BlobServiceClient = new BlobServiceClient("UseDevelopmentStorage=true");
            options.GetBlobName = (grainId) => $"{grainId.Type}-{grainId.Key}.bin";
        });
    })
    .Build();

await host.StartAsync();

var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
var grain = grainFactory.GetGrain<ITestGrain>("key");

int i = 1;
while (i <= 50)
{
    await grain.Ping();
    await Task.Delay(1000);
    i++;
}

Console.ReadKey();

public interface ITestGrain : IGrainWithStringKey
{
    Task Ping();
}

public class TestGrain(
    [FromKeyedServices("dict1")] IDurableValue<int> machine1
    //,[FromKeyedServices("dict2")] IDurableValue<int> machine2
        ) : DurableGrain, ITestGrain
{
    public async Task Ping()
    {
        Do("machine1", machine1);
        //Console.WriteLine("------------"); Do("machine2", machine2);

        await WriteStateAsync();
    }

    void Do(string name, IDurableValue<int> machine)
    {
        machine.Value = ++machine.Value;
        Console.WriteLine($"{name}: " + machine.Value);
    }
}
