using System;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Orleans.Configuration;

namespace Orleans.Connections.Security.Entra;

internal static class EntraSigningKey
{
    private const string CloudInstanceName = "cloud_instance_name";
    private const string Issuer = "issuer";
    private const string TenantIdTemplate = "{tenantid}";

    public static bool IsUsable(
        SecurityKey key,
        OpenIdConnectConfiguration configuration,
        EntraSiloConnectionOptions options)
    {
        return HasUsableKeyMaterial(key)
            && configuration.JsonWebKeySet?.Keys.Any(
                jsonWebKey => string.Equals(jsonWebKey.Kid, key.KeyId, StringComparison.Ordinal)
                    && HasMatchingKeyMaterial(key, jsonWebKey)
                    && IsUsable(jsonWebKey, options)) == true;
    }

    public static bool IsUsable(
        SecurityKey key,
        OpenIdConnectConfiguration configuration,
        EntraSiloConnectionOptions options,
        string tokenIssuer,
        string tenantId)
    {
        return HasUsableKeyMaterial(key)
            && configuration.JsonWebKeySet?.Keys.Any(
                jsonWebKey => string.Equals(jsonWebKey.Kid, key.KeyId, StringComparison.Ordinal)
                    && HasMatchingKeyMaterial(key, jsonWebKey)
                    && IsUsable(jsonWebKey, options)
                    && HasCompatibleIssuer(jsonWebKey, configuration, tokenIssuer, tenantId)
                    && HasCompatibleCloudInstance(jsonWebKey, configuration)) == true;
    }

    private static bool HasUsableKeyMaterial(SecurityKey key) =>
        key is AsymmetricSecurityKey && !string.IsNullOrEmpty(key.KeyId);

    private static bool HasMatchingKeyMaterial(SecurityKey key, JsonWebKey jsonWebKey)
    {
        if (key is X509SecurityKey x509SecurityKey && jsonWebKey.X5c is { Count: > 0 })
        {
            try
            {
                return CryptographicOperations.FixedTimeEquals(
                    x509SecurityKey.Certificate.RawData,
                    Convert.FromBase64String(jsonWebKey.X5c[0]));
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return key.CanComputeJwkThumbprint()
            && jsonWebKey.CanComputeJwkThumbprint()
            && CryptographicOperations.FixedTimeEquals(
                key.ComputeJwkThumbprint(),
                jsonWebKey.ComputeJwkThumbprint());
    }

    public static bool IsUsable(JsonWebKey jsonWebKey, EntraSiloConnectionOptions options)
    {
        if (!string.Equals(jsonWebKey.Use, JsonWebKeyUseNames.Sig, StringComparison.Ordinal)
            || (jsonWebKey.KeyOps is { Count: > 0 }
                && !jsonWebKey.KeyOps.Contains("verify", StringComparer.Ordinal)))
        {
            return false;
        }

        if (!string.Equals(jsonWebKey.Kty, JsonWebAlgorithmsKeyTypes.RSA, StringComparison.Ordinal)
            && !string.Equals(jsonWebKey.Kty, JsonWebAlgorithmsKeyTypes.EllipticCurve, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrEmpty(jsonWebKey.Alg) || options.AllowedAlgorithms.Contains(jsonWebKey.Alg);
    }

    private static bool HasCompatibleIssuer(
        JsonWebKey jsonWebKey,
        OpenIdConnectConfiguration configuration,
        string tokenIssuer,
        string tenantId)
    {
        if (!TryGetMetadataValue(jsonWebKey.AdditionalData, Issuer, out var signingKeyIssuer))
        {
            return true;
        }

        if (!tokenIssuer.Contains(tenantId, StringComparison.Ordinal))
        {
            return false;
        }

        var effectiveSigningKeyIssuer = signingKeyIssuer.Replace(
            TenantIdTemplate,
            tenantId,
            StringComparison.Ordinal);
        var effectiveConfigurationIssuer = configuration.Issuer?.Replace(
            TenantIdTemplate,
            tenantId,
            StringComparison.Ordinal);
        return string.Equals(effectiveSigningKeyIssuer, tokenIssuer, StringComparison.Ordinal)
            || string.Equals(effectiveSigningKeyIssuer, effectiveConfigurationIssuer, StringComparison.Ordinal);
    }

    private static bool HasCompatibleCloudInstance(
        JsonWebKey jsonWebKey,
        OpenIdConnectConfiguration configuration)
    {
        return !TryGetMetadataValue(jsonWebKey.AdditionalData, CloudInstanceName, out var signingKeyCloudInstance)
            || !TryGetMetadataValue(
                configuration.AdditionalData,
                CloudInstanceName,
                out var configurationCloudInstance)
            || string.Equals(
                signingKeyCloudInstance,
                configurationCloudInstance,
                StringComparison.Ordinal);
    }

    private static bool TryGetMetadataValue(
        IDictionary<string, object> metadata,
        string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        if (metadata.TryGetValue(name, out var rawValue)
            && rawValue is string candidate
            && !string.IsNullOrWhiteSpace(candidate))
        {
            value = candidate;
            return true;
        }

        value = null;
        return false;
    }
}
