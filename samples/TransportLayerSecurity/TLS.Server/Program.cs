using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using System.Security.Cryptography.X509Certificates;
using HelloWorld.Grains;
using HelloWorld.Interfaces;

await new HostBuilder()
    .UseEnvironment(Environments.Development)
    .UseOrleans((ctx, builder) =>
    {
        var isDevelopment = ctx.HostingEnvironment.IsDevelopment();
        builder
            .UseLocalhostClustering()
            .UseTls(
                StoreName.My,
                "fakedomain.faketld",
                allowInvalid: isDevelopment,
                StoreLocation.CurrentUser,
                options =>
                {
                    // In this sample there is only one silo, however if there are multiple silos then the TargetHost must be set
                    // for each connection which is initiated.
                    options.OnAuthenticateAsClient = (connection, sslOptions) =>
                    {
                        sslOptions.TargetHost = "fakedomain.faketld";
                    };

                    if (isDevelopment)
                    {
                        // NOTE: Do not do this in a production environment
                        options.AllowAnyRemoteCertificate();
                    }
                })
            .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(HelloGrain).Assembly).AddApplicationPart(typeof(IHelloGrain).Assembly));
    })
    .ConfigureLogging(logging => logging.AddConsole())
    .RunConsoleAsync();
