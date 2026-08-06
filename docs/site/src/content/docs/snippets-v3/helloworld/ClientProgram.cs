using HelloWorld.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;

namespace HelloWorld;

public class ClientProgram
{
    private static int _attempt = 0;

    // <client_setup>
    static async Task<IClusterClient> StartClientWithRetries()
    {
        _attempt = 0;
        var client = new ClientBuilder()
            .UseLocalhostClustering()
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "dev";
                options.ServiceId = "HelloWorldApp";
            })
            .ConfigureLogging(logging => logging.AddConsole())
            .Build();

        await client.Connect(RetryFilter);
        Console.WriteLine("Client successfully connected to silo host");
        return client;
    }

    private static async Task<bool> RetryFilter(Exception exception)
    {
        if (exception.GetType() != typeof(SiloUnavailableException))
        {
            Console.WriteLine($"Cluster client failed to connect to cluster with unexpected error. Exception: {exception}");
            return false;
        }
        _attempt++;
        Console.WriteLine($"Cluster client attempt {_attempt} of 5 failed to connect to cluster. Exception: {exception}");
        if (_attempt > 5)
        {
            return false;
        }
        await Task.Delay(TimeSpan.FromSeconds(4));
        return true;
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

    public static async Task ClientMain(string[] args)
    {
        var client = await StartClientWithRetries();
        await DoClientWork(client);
        await client.Close();
    }
}
