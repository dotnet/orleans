using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Security;
using Orleans.Hosting;

using var certificate = SampleCertificates.CreateDevelopmentCertificate("localhost");
using IHost host = Host.CreateDefaultBuilder(args)
    .UseEnvironment(Environments.Development)
    .UseOrleans(builder =>
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
Console.WriteLine("SiloExample started with development TLS. Press Ctrl+C to exit.");
await host.WaitForShutdownAsync();

class BasicExample
{
    public static async Task ConfigureBasicTls()
    {
        // <BasicTlsConfiguration>
        using IHost host = Host.CreateDefaultBuilder()
            .UseOrleans(builder =>
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
        // </BasicTlsConfiguration>
    }
}

class DevelopmentExample
{
    public static async Task ConfigureDevelopmentTls()
    {
        // <DevelopmentTlsConfiguration>
        var hostBuilder = Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Development);

        using IHost host = hostBuilder
            .UseOrleans((context, builder) =>
            {
                var isDevelopment = context.HostingEnvironment.IsDevelopment();

                builder
                    .UseLocalhostClustering()
                    .UseTls(StoreName.My, "localhost", allowInvalid: isDevelopment, StoreLocation.CurrentUser, options =>
                    {
                        options.OnAuthenticateAsClient = (connection, sslOptions) =>
                        {
                            sslOptions.TargetHost = "localhost";
                        };

                        if (isDevelopment)
                        {
                            options.AllowAnyRemoteCertificate();
                        }
                    });
            })
            .ConfigureLogging(logging => logging.AddConsole())
            .Build();

        await host.RunAsync();
        // </DevelopmentTlsConfiguration>
    }
}

class CertificateExample
{
    public static async Task ConfigureTlsWithCertificate()
    {
        // <CertificateTlsConfiguration>
        using var cert = X509CertificateLoader.LoadPkcs12FromFile("path/to/certificate.pfx", "password");

        using IHost host = Host.CreateDefaultBuilder()
            .UseOrleans(builder =>
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
        // </CertificateTlsConfiguration>
    }
}

class AdvancedExample
{
    public static async Task ConfigureAdvancedTls()
    {
        // <AdvancedTlsConfiguration>
        using IHost host = Host.CreateDefaultBuilder()
            .UseOrleans(builder =>
            {
                builder
                    .UseLocalhostClustering()
                    .UseTls(StoreName.My, "my-certificate-subject", allowInvalid: false, StoreLocation.LocalMachine, options =>
                    {
                        options.LocalServerCertificateSelector = (connection, serverName) =>
                        {
                            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                            store.Open(OpenFlags.ReadOnly);

                            var certificates = store.Certificates.Find(
                                X509FindType.FindBySubjectName,
                                serverName ?? "my-certificate-subject",
                                validOnly: true);

                            return certificates.Count > 0 ? certificates[0] : null;
                        };

                        options.RemoteCertificateValidation = (certificate, chain, sslPolicyErrors) =>
                            sslPolicyErrors == SslPolicyErrors.None;

                        options.OnAuthenticateAsClient = (connection, sslOptions) =>
                        {
                            sslOptions.TargetHost = "my-certificate-subject";
                        };

                        options.CheckCertificateRevocation = true;
                    });
            })
            .ConfigureLogging(logging => logging.AddConsole())
            .Build();

        await host.RunAsync();
        // </AdvancedTlsConfiguration>
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
