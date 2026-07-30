using HelloWorld.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace HelloWorld;

public class ClientProgram
{
    // <client_setup>
    public static async Task ClientMain(string[] args)
    {
        using IHost host = Host.CreateDefaultBuilder(args)
            .UseOrleansClient(clientBuilder =>
            {
                clientBuilder.UseLocalhostClustering()
                    .Configure<ClusterOptions>(options =>
                    {
                        options.ClusterId = "dev";
                        options.ServiceId = "HelloWorldApp";
                    });
            })
            .ConfigureLogging(logging => logging.AddConsole())
            .Build();

        await host.StartAsync();

        var client = host.Services.GetRequiredService<IClusterClient>();
        Console.WriteLine("Client successfully connected to silo host");

        await DoClientWork(client);

        await host.StopAsync();
    }
    // </client_setup>

    // <do_client_work>
    static async Task DoClientWork(IClusterClient client)
    {
        var friend = client.GetGrain<IHello>(0);
        var response = await friend.SayHello("Good morning, my friend!");
        Console.WriteLine($"\n\n{response}\n\n");
    }
    // </do_client_work>
}
