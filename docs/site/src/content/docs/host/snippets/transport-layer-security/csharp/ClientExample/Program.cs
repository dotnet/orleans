using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Security;
using Orleans.Hosting;

using var certificate = SampleCertificates.CreateDevelopmentCertificate("localhost");
using IHost host = Host.CreateDefaultBuilder(args)
    .UseEnvironment(Environments.Development)
    .UseOrleansClient(builder =>
    {
        builder
            .UseLocalhostClustering()
            .UseTls(certificate, options =>
            {
                options.AllowAnyRemoteCertificate();
                options.OnAuthenticateAsClient = (connection, sslOptions) =>
                {
                    sslOptions.TargetHost = "localhost";
                };
            });
    })
    .ConfigureLogging(logging => logging.AddConsole())
    .Build();

await host.StartAsync();
Console.WriteLine("ClientExample connected with development TLS. Press Ctrl+C to exit.");
await host.WaitForShutdownAsync();

class BasicClientExample
{
    public static async Task ConfigureBasicTls()
    {
        // <BasicClientTlsConfiguration>
        using IHost host = Host.CreateDefaultBuilder()
            .UseOrleansClient(builder =>
            {
                builder
                    .UseLocalhostClustering()
                    .UseTls(StoreName.My, "my-certificate-subject", allowInvalid: false, StoreLocation.CurrentUser, options =>
                    {
                        options.OnAuthenticateAsClient = (connection, sslOptions) =>
                        {
                            sslOptions.TargetHost = "my-certificate-subject";
                        };
                    });
            })
            .ConfigureLogging(logging => logging.AddConsole())
            .Build();

        await host.RunAsync();
        // </BasicClientTlsConfiguration>
    }
}

class ClientDevelopmentExample
{
    public static async Task ConfigureDevelopmentTls()
    {
        // <ClientDevelopmentTlsConfiguration>
        var hostBuilder = Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Development);

        using IHost host = hostBuilder
            .UseOrleansClient((context, builder) =>
            {
                var isDevelopment = context.HostingEnvironment.IsDevelopment();

                builder
                    .UseLocalhostClustering()
                    .UseTls(StoreName.My, "localhost", allowInvalid: isDevelopment, StoreLocation.CurrentUser, options =>
                    {
                        if (isDevelopment)
                        {
                            options.AllowAnyRemoteCertificate();
                        }

                        options.OnAuthenticateAsClient = (connection, sslOptions) =>
                        {
                            sslOptions.TargetHost = "localhost";
                        };
                    });
            })
            .ConfigureLogging(logging => logging.AddConsole())
            .Build();

        await host.RunAsync();
        // </ClientDevelopmentTlsConfiguration>
    }
}

class ClientCertificateExample
{
    public static async Task ConfigureTlsWithCertificate()
    {
        // <ClientCertificateTlsConfiguration>
        using var cert = X509CertificateLoader.LoadPkcs12FromFile("path/to/certificate.pfx", "password");

        using IHost host = Host.CreateDefaultBuilder()
            .UseOrleansClient(builder =>
            {
                builder
                    .UseLocalhostClustering()
                    .UseTls(cert, options =>
                    {
                        options.OnAuthenticateAsClient = (connection, sslOptions) =>
                        {
                            sslOptions.TargetHost = cert.GetNameInfo(X509NameType.DnsName, false) ?? "my-certificate-subject";
                        };
                    });
            })
            .ConfigureLogging(logging => logging.AddConsole())
            .Build();

        await host.RunAsync();
        // </ClientCertificateTlsConfiguration>
    }
}

static class SampleCertificates
{
    public static X509Certificate2 CreateDevelopmentCertificate(string dnsName)
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={dnsName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName(dnsName);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        var usages = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"),
            new("1.3.6.1.5.5.7.3.2"),
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: true));

        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        var password = Guid.NewGuid().ToString("N");
        // Windows server-side TLS requires a key container instead of an ephemeral key.
        return X509CertificateLoader.LoadPkcs12(
            created.Export(X509ContentType.Pfx, password),
            password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet,
            Pkcs12LoaderLimits.Defaults);
    }
}
