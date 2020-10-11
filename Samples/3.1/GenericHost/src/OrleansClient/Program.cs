using HelloWorld.Interfaces;
using Orleans;
using Orleans.Runtime;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace OrleansClient
{
    /// <summary>
    /// Orleans test silo client
    /// </summary>
    public class Program
    {
        static async Task<int> Main(string[] args)
        {
            try
            {
                // Configure a client and connect to the service.
                var client = new ClientBuilder()
                    .UseLocalhostClustering()
                    .Configure<ClusterOptions>(options =>
                    {
                        options.ClusterId = "dev";
                        options.ServiceId = "HelloWorldApp";
                    })
                    .ConfigureLogging(logging => logging.AddConsole())
                    .Build();

                await client.Connect(CreateRetryFilter());
                Console.WriteLine("Client successfully connect to silo host");

                // Use the connected client to call a grain, writing the result to the terminal.
                var friend = client.GetGrain<IHello>(0);
                var response = await friend.SayHello("Good morning, my friend!");
                Console.WriteLine("\n\n{0}\n\n", response);

                Console.ReadKey();
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Console.ReadKey();
                return 1;
            }
        }

        private static Func<Exception, Task<bool>> CreateRetryFilter(int maxAttempts = 5)
        {
            var attempt = 0;
            return RetryFilter;

            async Task<bool> RetryFilter(Exception exception)
            {
                attempt++;
                Console.WriteLine($"Cluster client attempt {attempt} of {maxAttempts} failed to connect to cluster.  Exception: {exception}");
                if (attempt > maxAttempts)
                {
                    return false;
                }

                await Task.Delay(TimeSpan.FromSeconds(4));
                return true;
            }
        }
    }
}
