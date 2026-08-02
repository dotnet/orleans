using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

Console.WriteLine("TLS configuration examples");

internal static class TlsExamples
{
    public static IHost CreateServerAuthenticatedClient()
    {
        // <ServerAuthenticatedTls>
        var builder = Host.CreateApplicationBuilder();

        builder.UseOrleansClient(clientBuilder =>
        {
            clientBuilder
                .UseLocalhostClustering()
                .UseTls(options =>
                {
                    options.OnAuthenticateAsClient = (_, sslOptions) =>
                    {
                        sslOptions.TargetHost = "orleans.example.net";
                    };
                    options.CheckCertificateRevocation = true;
                });
        });

        return builder.Build();
        // </ServerAuthenticatedTls>
    }

    public static IHost CreateMutualTlsClient(X509Certificate2 clientCertificate)
    {
        // <MutualTls>
        var builder = Host.CreateApplicationBuilder();

        builder.UseOrleansClient(clientBuilder =>
        {
            clientBuilder
                .UseLocalhostClustering()
                .UseTls(clientCertificate, options =>
                {
                    options.OnAuthenticateAsClient = (_, sslOptions) =>
                    {
                        sslOptions.TargetHost = "orleans.example.net";
                    };
                    options.CheckCertificateRevocation = true;
                });
        });

        return builder.Build();
        // </MutualTls>
    }
}
