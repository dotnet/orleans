var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans();
using var app = builder.Build();

await app.StartAsync();
var client = app.Services.GetRequiredService<IGrainFactory>();

var counter = client.GetGrain<ICounterGrain>("blah");
for (var i = 0; i < 10; i++)
{
    Console.WriteLine($"Count: {await counter.Increment()}");
    await Task.Delay(1000);
}

await app.StopAsync();

public interface ICounterGrain : IGrainWithStringKey
{
    ValueTask<int> Increment();
}

public class CounterGrain : Grain, ICounterGrain
{
    private int _count;
    public ValueTask<int> Increment() => new(++_count);
}
