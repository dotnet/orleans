var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleansClient();
using var app = builder.Build();

await app.StartAsync();
var client = app.Services.GetRequiredService<IGrainFactory>();

var counter = client.GetGrain<ICounterGrain>("blah");
for (var i = 0; i < 10; i++)
{
    Console.WriteLine($"Count: {await counter.Increment()}");
    await Task.Delay(1000);
}

await app.WaitForShutdownAsync();
