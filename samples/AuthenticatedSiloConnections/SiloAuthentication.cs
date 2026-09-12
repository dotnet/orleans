using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Orleans.Connections.Security;
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
                entra.TokenScope = options.Entra.TokenScope;
                entra.ResourceApplicationId = options.Entra.ResourceApplicationId;
                entra.ValidTenantIds.Add(options.Entra.TenantId);
                entra.ClusterRole = requiredRole;

                foreach (var clientId in allowedCallerClientIds)
                {
                    entra.AllowedClientIds.Add(clientId);
                }
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
