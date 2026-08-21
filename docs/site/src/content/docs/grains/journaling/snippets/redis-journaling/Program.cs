using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using StackExchange.Redis;

var redisConnection = args.FirstOrDefault() ?? "localhost:6379";
var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddRedisJournalStorage(options =>
        {
            options.ConfigurationOptions =
                ConfigurationOptions.Parse(redisConnection);
            options.KeyPrefix = "journaling-docs-sample";
        })
        .UseJsonJournalFormat(RedisJournalJsonContext.Default);
});

using var host = builder.Build();
await host.StartAsync();

var grain = host.Services.GetRequiredService<IGrainFactory>()
    .GetGrain<IRedisJournalCounterGrain>("demo");
var written = await grain.Increment();

await grain.Deactivate();
await Task.Delay(TimeSpan.FromMilliseconds(500));

var recovered = await grain.GetSnapshot();
if (written.ActivationId == recovered.ActivationId ||
    written.Count != recovered.Count)
{
    throw new InvalidOperationException("Redis journal recovery failed.");
}

Console.WriteLine($"Recovered count: {recovered.Count}");
await host.StopAsync();

public interface IRedisJournalCounterGrain : IGrainWithStringKey
{
    ValueTask<CounterSnapshot> Increment();

    ValueTask<CounterSnapshot> GetSnapshot();

    ValueTask Deactivate();
}

// <redis_journal_counter>
public sealed class RedisJournalCounterGrain(
    [FromKeyedServices("count")] IDurableValue<int> count)
    : DurableGrain, IRedisJournalCounterGrain
{
    private readonly Guid _activationId = Guid.NewGuid();

    public async ValueTask<CounterSnapshot> Increment()
    {
        count.Value++;
        await WriteStateAsync();
        return GetCurrentSnapshot();
    }

    public ValueTask<CounterSnapshot> GetSnapshot() =>
        ValueTask.FromResult(GetCurrentSnapshot());

    public ValueTask Deactivate()
    {
        DeactivateOnIdle();
        return ValueTask.CompletedTask;
    }

    private CounterSnapshot GetCurrentSnapshot() =>
        new(_activationId, count.Value);
}
// </redis_journal_counter>

[GenerateSerializer]
public sealed record CounterSnapshot(
    [property: Id(0)] Guid ActivationId,
    [property: Id(1)] int Count);

[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ulong))]
internal partial class RedisJournalJsonContext : JsonSerializerContext;
