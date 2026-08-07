using System.Net.Security;
using System.Security.Cryptography;
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
        var trustedRoots = CertificatePolicy.ParseSha256Fingerprints(
            options.Certificate.TrustedRootSha256Fingerprints);

        // <AuthenticatedSiloConnections>
        siloBuilder.UseAuthenticatedSiloConnections(
            tls =>
            {
                tls.LocalCertificate = siloCertificate;
                tls.RemoteCertificateMode = RemoteCertificateMode.RequireCertificate;
                tls.ClientCertificateMode = RemoteCertificateMode.RequireCertificate;
                tls.CheckCertificateRevocation = true;
                tls.OnAuthenticateAsClient = (_, sslOptions) =>
                {
                    sslOptions.TargetHost = options.Certificate.TargetHost;
                    sslOptions.CertificateRevocationCheckMode =
                        X509RevocationMode.Online;
                };
                tls.RemoteCertificateValidation = (certificate, chain, errors) =>
                    CertificatePolicy.ValidateRemoteCertificate(
                        certificate,
                        chain,
                        errors,
                        trustedRoots);
            },
            authentication =>
            {
                authentication.Mode = options.AuthenticationMode;
                authentication.TokenExchangeTimeout = TimeSpan.FromSeconds(10);
                authentication.MaxTokenSize = 16 * 1024;
                authentication.MaxConcurrentHandshakes = 256;
                authentication.MinimumRemainingTokenLifetime =
                    TimeSpan.FromMinutes(2);

                authentication.UseEntra(
                    credential,
                    entra =>
                    {
                        entra.Authority = options.Entra.Authority;
                        entra.TokenScope = $"{options.Entra.Audience}/.default";
                        entra.ValidAudiences.Add(options.Entra.Audience);
                        entra.ValidTenantIds.Add(options.Entra.TenantId);

                        foreach (var clientId in options.Entra.AllowedCallerClientIds)
                        {
                            entra.AllowedClientIds.Add(clientId);
                        }

                        entra.RequiredRoles.Add("Orleans.Silo.Connect");
                    });
            });
        // </AuthenticatedSiloConnections>
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

    public static byte[][] ParseSha256Fingerprints(IEnumerable<string> values)
        => values.Select(value =>
        {
            var normalized = value.Replace(":", "", StringComparison.Ordinal);
            if (normalized.Length != 64)
            {
                throw new InvalidOperationException(
                    "Every trusted root fingerprint must contain 32 SHA-256 bytes.");
            }

            try
            {
                return Convert.FromHexString(normalized);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException(
                    "A trusted root fingerprint is not hexadecimal.",
                    exception);
            }
        }).ToArray();

    public static bool ValidateRemoteCertificate(
        X509Certificate2 certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        IReadOnlyList<byte[]> trustedRootFingerprints)
    {
        if (errors != SslPolicyErrors.None
            || chain is null
            || chain.ChainElements.Count == 0)
        {
            return false;
        }

        var root = chain.ChainElements[^1].Certificate;
        var rootFingerprint = root.GetCertHash(HashAlgorithmName.SHA256);
        return trustedRootFingerprints.Any(
            expected => CryptographicOperations.FixedTimeEquals(
                expected,
                rootFingerprint));
    }
}
