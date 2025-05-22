using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Core.Internal;
using Orleans.Journaling;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Error))
    .UseOrleans(builder =>
    {
        builder.UseLocalhostClustering();
        builder.AddCosmosLogStorage(c =>
        {
            c.IsResourceCreationEnabled = true;
            c.CleanResourcesOnInitialization = false;
            c.ConfigureCosmosClient("AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;DisableServerCertificateValidation=True");
        });
    })
    .Build();

await host.StartAsync();

var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
var grain = grainFactory.GetGrain<ITestDurableGrain>(Guid.Parse("1a8a7965-1d5a-43d7-9d6e-524fea10798b"));

int i = 1;

while (i < 10)
{
    await grain.SetTestValues("Test Name", 42);
    await Task.Delay(100);
    i++;
}

var name = await grain.GetName();
var counter = await grain.GetCounter();

await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

name = await grain.GetName();
counter = await grain.GetCounter();

Console.WriteLine("Done");


[GenerateSerializer]
public sealed record TestDurableGrainState(string Name, int Counter);

public interface ITestDurableGrain : IGrainWithGuidKey
{
    Task<Guid> GetActivationId();
    Task SetTestValues(string name, int counter);
    Task<string> GetName();
    Task<int> GetCounter();
}


public class TestDurableGrain(
    [FromKeyedServices("state")] IPersistentState<TestDurableGrainState> state)
        : DurableGrain, ITestDurableGrain
{
    private readonly Guid _activationId = Guid.NewGuid();
    public Task<string> GetName() => Task.FromResult(state.State.Name);
    public Task<int> GetCounter() => Task.FromResult(state.State.Counter);

    public async Task SetTestValues(string name, int counter)
    {
        state.State = new(name, counter);
        await WriteStateAsync();
    }

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);
}
