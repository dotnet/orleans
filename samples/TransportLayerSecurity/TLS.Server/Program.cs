using HelloWorld;
using HelloWorld.Grains;
using HelloWorld.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography.X509Certificates;

// Ensure the self-signed certificate exists (creates one if missing)
CertificateHelper.EnsureCertificateExists();

await new HostBuilder()
    .UseEnvironment(Environments.Development)
    .UseOrleans((ctx, builder) =>
    {
        var isDevelopment = ctx.HostingEnvironment.IsDevelopment();
        builder
            .UseLocalhostClustering()
            .UseTls(
                CertificateHelper.StoreName,
                CertificateHelper.CertificateSubject,
                allowInvalid: isDevelopment,
                CertificateHelper.StoreLocation,
                options =>
                {
                    // In this sample there is only one silo, however if there are multiple silos then the TargetHost must be set
                    // for each connection which is initiated.
                    options.OnAuthenticateAsClient = (connection, sslOptions) => sslOptions.TargetHost = CertificateHelper.CertificateSubject;

                    if (isDevelopment)
                    {
                        // NOTE: Do not do this in a production environment
                        options.AllowAnyRemoteCertificate();
                    }
                });
    })
    .ConfigureLogging(logging => logging.AddConsole())
    .RunConsoleAsync();
