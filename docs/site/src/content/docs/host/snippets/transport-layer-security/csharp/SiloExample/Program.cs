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
                    options.RemoteCertificateMode =
                        RemoteCertificateMode.NoCertificate;
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
                    options.CheckCertificateRevocation = true;
                });
        });

        return builder.Build();
        // </MutualTls>
    }
}
