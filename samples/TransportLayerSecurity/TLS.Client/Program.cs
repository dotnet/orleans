using HelloWorld;
using HelloWorld.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Security.Cryptography.X509Certificates;

// Ensure the self-signed certificate exists (creates one if missing)
CertificateHelper.EnsureCertificateExists();

// Configure a client and connect to the service.
using var host = new HostBuilder()
    .UseOrleansClient(builder =>
        builder.UseLocalhostClustering()
            .UseTls(CertificateHelper.StoreName, CertificateHelper.CertificateSubject,
                allowInvalid: true,
                CertificateHelper.StoreLocation,
                options =>
                {
                    options.OnAuthenticateAsClient =
                        (connection, sslOptions) =>
                            sslOptions.TargetHost = CertificateHelper.CertificateSubject;

                    // NOTE: Do not do this in a production environment, since it is insecure.
                    options.AllowAnyRemoteCertificate();
                }))
    .UseConsoleLifetime()
    .Build();

await host.StartAsync();
Console.WriteLine("Client successfully connected to silo host");

// Use the connected client to call a grain, writing the result to the terminal.
var factory = host.Services.GetRequiredService<IGrainFactory>();
var friend = factory.GetGrain<IHelloGrain>(0);
var response = await friend.SayHello("Good morning, my friend!");
Console.WriteLine($"\n\n{response}\n\n");

Console.WriteLine("Press Enter to exit...");
Console.ReadLine();
