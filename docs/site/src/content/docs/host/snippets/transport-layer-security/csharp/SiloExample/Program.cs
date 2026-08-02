using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Orleans.Connections.Security;
using Orleans.Hosting;

Console.WriteLine("TLS configuration examples");

internal static class TlsExamples
{
    public static IHost CreateServerAuthenticatedSilo(X509Certificate2 serverCertificate)
    {
        // <ServerAuthenticatedTls>
        var builder = Host.CreateApplicationBuilder();

        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .UseLocalhostClustering()
                .UseTls(serverCertificate, options =>
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

    public static IHost CreateMutualTlsSilo(X509Certificate2 siloCertificate)
    {
        // <MutualTls>
        var builder = Host.CreateApplicationBuilder();

        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .UseLocalhostClustering()
                .UseTls(siloCertificate, options =>
                {
                    options.ClientCertificateMode =
                        RemoteCertificateMode.RequireCertificate;
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
