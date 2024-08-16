using ClassLibrary1;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args).CreateHost(2);
await host.StartAsync();
await host.WaitTillClusterIsUp();

while (true)
{
    await Task.Delay(1000);
}