using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Orleans.Connections.Transport.Security;
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
                    options.RemoteCertificateMode =
                        RemoteCertificateMode.RequireCertificate;
                    options.ClientCertificateMode =
                        RemoteCertificateMode.NoCertificate;
                    options.OnAuthenticateAsClient = (_, sslOptions) =>
                    {
                        sslOptions.TargetHost = "orleans.example.net";
                        sslOptions.CertificateRevocationCheckMode =
                            X509RevocationMode.Online;
                    };
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
                    options.RemoteCertificateMode =
                        RemoteCertificateMode.RequireCertificate;
                    options.ClientCertificateMode =
                        RemoteCertificateMode.RequireCertificate;
                    options.OnAuthenticateAsClient = (_, sslOptions) =>
                    {
                        sslOptions.TargetHost = "orleans.example.net";
                        sslOptions.CertificateRevocationCheckMode =
                            X509RevocationMode.Online;
                    };
                });
        });

        return builder.Build();
        // </MutualTls>
    }
}
