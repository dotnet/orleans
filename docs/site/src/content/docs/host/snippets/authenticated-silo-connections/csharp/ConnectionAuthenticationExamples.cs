using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Orleans.Connections.Security;
using Orleans.Connections.Security.Entra;
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
                    "Orleans.Silo.Connect");
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
                    "Orleans.Client.Connect");
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
                    "Orleans.Client.Connect");
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

        authentication.UseEntra(
            credential,
            entra =>
            {
                entra.Authority = options.Entra.Authority;
                entra.TokenScope = $"{options.Entra.Audience}/.default";
                entra.ValidAudiences.Add(options.Entra.Audience);
                entra.ValidTenantIds.Add(options.Entra.TenantId);
                entra.ClusterAudienceFormat =
                    $"api://{options.Entra.ResourceApplicationId}/{{0}}";

                foreach (var clientId in allowedCallerClientIds)
                {
                    entra.AllowedClientIds.Add(clientId);
                }

                entra.RequiredRoles.Add(requiredRole);
            });
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
    public string TenantId { get; init; } = "";

    public string ResourceApplicationId { get; init; } = "";

    public string WorkloadClientId { get; init; } = "";

    public string FederatedTokenFile { get; init; } = "";

    public string Audience { get; init; } = "";

    public Uri Authority { get; init; } = null!;

    public string[] AllowedSiloCallerClientIds { get; init; } = [];

    public string[] AllowedClientCallerClientIds { get; init; } = [];
}
