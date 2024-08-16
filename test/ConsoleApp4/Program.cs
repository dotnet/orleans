using ClassLibrary1;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args).CreateHost(3);
await host.StartAsync();
await host.WaitTillClusterIsUp();

while (true)
{
    await Task.Delay(1000);
}