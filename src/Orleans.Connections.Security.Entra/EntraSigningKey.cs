using System;
using System.Linq;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Orleans.Configuration;

namespace Orleans.Connections.Security.Entra;

internal static class EntraSigningKey
{
    public static bool IsUsable(
        SecurityKey key,
        OpenIdConnectConfiguration configuration,
        EntraSiloConnectionOptions options)
    {
        if (key is not AsymmetricSecurityKey || string.IsNullOrEmpty(key.KeyId))
        {
            return false;
        }

        return configuration.JsonWebKeySet?.Keys.Any(
            jsonWebKey => string.Equals(jsonWebKey.Kid, key.KeyId, StringComparison.Ordinal)
                && IsUsable(jsonWebKey, options)) == true;
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
}
