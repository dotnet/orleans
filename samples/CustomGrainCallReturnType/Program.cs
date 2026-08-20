using CustomGrainCallReturnType;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using var host = Host.CreateDefaultBuilder(args)
    .UseOrleans(static siloBuilder => siloBuilder.UseLocalhostClustering())
    .Build();

await host.StartAsync();

var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
var calculator = grainFactory.GetGrain<ICalculatorGrain>("calculator");

var addition = calculator.Add(20, 22);
Console.WriteLine($"20 + 22 = {await addition}");

try
{
    await calculator.Fail("The grain reported a sample failure.");
}
catch (InvalidOperationException exception)
{
    Console.WriteLine($"Observed failure: {exception.Message}");
}

await host.StopAsync();
