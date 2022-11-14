using HelloWorld.Interfaces;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

// Configure a client and connect to the service.
using var host = new HostBuilder()
    .UseOrleansClient(builder =>
        builder.UseLocalhostClustering(serviceId: "HelloWorldApp", clusterId: "dev")
            .UseTls(StoreName.My, "fakedomain.faketld",
                allowInvalid: true,
                StoreLocation.CurrentUser,
                options =>
                {
                    options.OnAuthenticateAsClient =
                        (connection, sslOptions) =>
                            sslOptions.TargetHost = "fakedomain.faketld";

                    // NOTE: Do not do this in a production environment, since it is insecure.
                    options.AllowAnyRemoteCertificate();
                }))
    .UseConsoleLifetime()
    .Build();

await host.StartAsync();
Console.WriteLine("Client successfully connect to silo host");

// Use the connected client to call a grain, writing the result to the terminal.
var factory = host.Services.GetRequiredService<IGrainFactory>();
var friend = factory.GetGrain<IHelloGrain>(0);
var response = await friend.SayHello("Good morning, my friend!");
Console.WriteLine("\n\n{0}\n\n", response);
Console.ReadKey();
