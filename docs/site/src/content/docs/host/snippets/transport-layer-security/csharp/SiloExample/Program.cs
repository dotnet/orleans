using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Orleans.Connections.Transport.Security;
using Orleans.Hosting;

Console.WriteLine("TLS configuration examples");

internal static class TlsExamples
{
    // <CertificateStore>
    public static IHost CreateServerAuthenticatedSiloFromStore()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .UseLocalhostClustering()
                .UseTls(
                    StoreName.My,
                    "orleans.example.net",
                    allowInvalid: false,
                    StoreLocation.CurrentUser,
                    options =>
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
    }
    // </CertificateStore>

    // <LoadPkcs12Certificate>
    public static X509Certificate2 LoadPkcs12Certificate(
        string certificatePath,
        ReadOnlySpan<char> certificatePassword)
    {
        return X509CertificateLoader.LoadPkcs12FromFile(
            certificatePath,
            certificatePassword);
    }
    // </LoadPkcs12Certificate>

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
