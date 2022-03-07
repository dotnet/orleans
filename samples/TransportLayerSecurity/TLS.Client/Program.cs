using HelloWorld.Interfaces;
using Orleans;
using Orleans.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography.X509Certificates;

// Configure a client and connect to the service.
var client = new ClientBuilder()
    .UseLocalhostClustering(serviceId: "HelloWorldApp", clusterId: "dev")
    .UseTls(
        StoreName.My,
        "fakedomain.faketld",
        allowInvalid: true,
        StoreLocation.CurrentUser,
        options =>
        {
            options.OnAuthenticateAsClient = (connection, sslOptions) =>
            {
                sslOptions.TargetHost = "fakedomain.faketld";
            };

            // NOTE: Do not do this in a production environment, since it is insecure.
            options.AllowAnyRemoteCertificate();
        })
    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IHelloGrain).Assembly))
    .ConfigureLogging(logging => logging.AddConsole())
    .Build();

var attempt = 0;
const int maxAttempts = 5;
await client.Connect(RetryFilter);
Console.WriteLine("Client successfully connect to silo host");

// Use the connected client to call a grain, writing the result to the terminal.
var friend = client.GetGrain<IHelloGrain>(0);
var response = await friend.SayHello("Good morning, my friend!");
Console.WriteLine("\n\n{0}\n\n", response);
Console.ReadKey();

await client.Close();

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
