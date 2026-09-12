using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Orleans.Connections.Security;
using Orleans.Hosting;

namespace Orleans.Docs.ConnectionSecurity;

internal static class ConnectionAuthenticationExamples
{
    public static TokenCredential CreateCredential(ConnectionSecurityOptions options)
    {
        // <ExplicitCredential>
        TokenCredential credential = new WorkloadIdentityCredential(
            new WorkloadIdentityCredentialOptions
            {
                TenantId = options.Entra.TenantId,
                ClientId = options.Entra.WorkloadClientId,
                TokenFilePath = options.Entra.FederatedTokenFile,
            });
        // </ExplicitCredential>

        return credential;
    }

    public static void ConfigureSilo(
        ISiloBuilder siloBuilder,
        ConnectionSecurityOptions options,
        TokenCredential credential,
        X509Certificate2 siloCertificate)
    {
        // <AuthenticatedSiloConnections>
        siloBuilder.UseAuthenticatedSiloConnections(
            tls =>
            {
                tls.LocalCertificate = siloCertificate;
                tls.RemoteCertificateMode = RemoteCertificateMode.RequireCertificate;
                tls.ClientCertificateMode = RemoteCertificateMode.RequireCertificate;
                tls.CheckCertificateRevocation = true;
            },
            authentication =>
            {
                ConfigureAuthentication(
                    authentication,
                    options,
                    credential,
                    options.Entra.AllowedSiloCallerClientIds,
                    options.Entra.SiloClusterRole);
            });
        // </AuthenticatedSiloConnections>

        // <AuthenticatedClientGateway>
        siloBuilder.UseAuthenticatedClientConnections(
            tls =>
            {
                tls.LocalCertificate = siloCertificate;
                tls.RemoteCertificateMode = RemoteCertificateMode.NoCertificate;
            },
            authentication =>
            {
                ConfigureAuthentication(
                    authentication,
                    options,
                    credential,
                    options.Entra.AllowedClientCallerClientIds,
                    options.Entra.ClientClusterRole);
            });
        // </AuthenticatedClientGateway>
    }

    public static void ConfigureClient(
        IClientBuilder clientBuilder,
        ConnectionSecurityOptions options,
        TokenCredential credential)
    {
        // <AuthenticatedClient>
        clientBuilder.UseAuthenticatedClientConnections(
            tls =>
            {
                tls.CheckCertificateRevocation = true;
            },
            authentication =>
            {
                ConfigureAuthentication(
                    authentication,
                    options,
                    credential,
                    options.Entra.AllowedClientCallerClientIds,
                    options.Entra.ClientClusterRole);
            });
        // </AuthenticatedClient>
    }

    public static void ConfigureDiagnostics(
        HostApplicationBuilder builder,
        bool exportToOtlp)
    {
        // <FixedDiagnostics>
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(console =>
        {
            console.TimestampFormat = "O";
            console.JsonWriterOptions = new() { Indented = false };
        });
        builder.Logging.AddFilter("Orleans.Connections.Security", LogLevel.Information);
        builder.Logging.AddFilter("Azure.Identity", LogLevel.Warning);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: "authenticated-orleans-silo",
                serviceInstanceId: Environment.MachineName))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter("Microsoft.Orleans.Connections.Security");

                if (exportToOtlp)
                {
                    metrics.AddOtlpExporter();
                }
            });
        // </FixedDiagnostics>
    }

    private static void ConfigureAuthentication(
        SiloConnectionAuthenticationBuilder authentication,
        ConnectionSecurityOptions options,
        TokenCredential credential,
        IEnumerable<string> allowedCallerClientIds,
        string requiredRole)
    {
        authentication.Mode = options.AuthenticationMode;
        authentication.TargetHost = options.Certificate.TargetHost;
        authentication.TokenExchangeTimeout = TimeSpan.FromSeconds(10);
        authentication.MaxTokenSize = 16 * 1024;
        authentication.MaxConcurrentInboundAuthentications = 256;
        authentication.MaxConcurrentOutboundAuthentications = 256;
        authentication.MaxPendingInboundAuthentications = 256;
        authentication.MaxPendingOutboundAuthentications = 256;
        authentication.MinimumRemainingTokenLifetime = TimeSpan.FromMinutes(2);

        // <EntraAuthenticationOptions>
        authentication.UseEntra(
            credential,
            entra =>
            {
                entra.Authority = options.Entra.Authority;
                entra.TokenScope = options.Entra.TokenScope;
                entra.ResourceApplicationId = options.Entra.ResourceApplicationId;
                entra.ValidTenantIds.Add(options.Entra.TenantId);
                entra.ClusterRole = requiredRole;

                foreach (var clientId in allowedCallerClientIds)
                {
                    entra.AllowedClientIds.Add(clientId);
                }
            });
        // </EntraAuthenticationOptions>
    }
}

internal sealed class ConnectionSecurityOptions
{
    public SiloConnectionAuthenticationMode AuthenticationMode { get; init; }

    public CertificateOptions Certificate { get; init; } = new();

    public EntraOptions Entra { get; init; } = new();
}

internal sealed class CertificateOptions
{
    public string TargetHost { get; init; } = "";
}

internal sealed class EntraOptions
{
    public string TenantId { get; init; } = "22222222-2222-2222-2222-222222222222";

    public string TokenScope { get; init; }
        = "api://11111111-1111-1111-1111-111111111111/contoso-prod-westus";

    public string ResourceApplicationId { get; init; }
        = "11111111-1111-1111-1111-111111111111";

    public string SiloClusterRole { get; init; }
        = "Orleans.Silo.Connect.contoso-prod-westus";

    public string ClientClusterRole { get; init; }
        = "Orleans.Client.Connect.contoso-prod-westus";

    public string WorkloadClientId { get; init; }
        = "33333333-3333-3333-3333-333333333333";

    public string FederatedTokenFile { get; init; } = "<federated-token-file>";

    public Uri Authority { get; init; }
        = new("https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222/v2.0");

    public string[] AllowedSiloCallerClientIds { get; init; } = [];

    public string[] AllowedClientCallerClientIds { get; init; } = [];
}
