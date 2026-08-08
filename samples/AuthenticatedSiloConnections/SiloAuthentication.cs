using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Orleans.Connections.Security;
using Orleans.Connections.Security.Entra;
using Orleans.Hosting;

namespace AuthenticatedSiloConnections;

internal static class SiloAuthentication
{
    public static void Configure(
        ISiloBuilder siloBuilder,
        SampleOptions options,
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

    internal static void ConfigureAuthentication(
        SiloConnectionAuthenticationBuilder authentication,
        SampleOptions options,
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

internal static class CertificatePolicy
{
    public static X509Certificate2 LoadSiloCertificate(
        string path,
        string? password)
        => X509CertificateLoader.LoadPkcs12FromFile(
            path,
            password,
            X509KeyStorageFlags.EphemeralKeySet);
}
