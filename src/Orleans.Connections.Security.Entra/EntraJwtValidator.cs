using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Orleans.Configuration;

namespace Orleans.Connections.Security.Entra;

internal readonly record struct EntraTokenValidationOutcome(
    ClaimsPrincipal Principal,
    DateTimeOffset ExpiresAt);

internal sealed class EntraJwtValidator
{
    private const string ApplicationIdentityType = "app";
    private readonly EntraSiloConnectionOptions _options;
    private readonly EntraOpenIdConfigurationProvider _configurationProvider;
    private readonly TimeProvider _timeProvider;
    private readonly JsonWebTokenHandler _handler;

    public EntraJwtValidator(
        EntraSiloConnectionOptions options,
        EntraOpenIdConfigurationProvider configurationProvider,
        TimeProvider timeProvider)
    {
        _options = options;
        _configurationProvider = configurationProvider;
        _timeProvider = timeProvider;
        _handler = new JsonWebTokenHandler
        {
            MapInboundClaims = false,
            MaximumTokenSizeInBytes = options.MaximumTokenSize,
        };
    }

    public async ValueTask<EntraTokenValidationOutcome> ValidateAsync(
        string token,
        string clusterId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token)
            || string.IsNullOrEmpty(clusterId)
            || Encoding.UTF8.GetByteCount(token) > _options.MaximumTokenSize)
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.InvalidToken);
        }

        JwtDocument document;
        try
        {
            document = JwtDocument.Parse(token);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or JsonException)
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.InvalidToken);
        }

        if (!_options.AllowedAlgorithms.Contains(document.Algorithm)
            || string.Equals(document.Algorithm, SecurityAlgorithms.None, StringComparison.Ordinal)
            || string.IsNullOrEmpty(document.KeyId))
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.InvalidToken);
        }

        ValidateUntrustedClaims(document, clusterId);
        var snapshot = await _configurationProvider.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var result = await ValidateSignatureAndStandardClaimsAsync(token, document, snapshot).ConfigureAwait(false);

        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            snapshot = await _configurationProvider.RefreshForUnknownSigningKeyAsync(
                snapshot.Generation,
                cancellationToken).ConfigureAwait(false);
            result = await ValidateSignatureAndStandardClaimsAsync(token, document, snapshot).ConfigureAwait(false);
        }

        if (!result.IsValid)
        {
            throw new EntraAuthenticationException(ClassifyValidationFailure(result.Exception));
        }

        ValidateTrustedClaims(document, snapshot.Configuration.Issuer, clusterId);

        var claimsIdentity = result.ClaimsIdentity
            ?? throw new EntraAuthenticationException(EntraAuthenticationError.InvalidToken);
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claimsIdentity.Claims.Select(static claim => claim.Clone()), "Entra", "name", "roles"));
        return new EntraTokenValidationOutcome(principal, DateTimeOffset.FromUnixTimeSeconds(document.ExpiresAt));
    }

    private Task<TokenValidationResult> ValidateSignatureAndStandardClaimsAsync(
        string token,
        JwtDocument document,
        EntraOpenIdConfigurationSnapshot snapshot)
    {
        var validAudiences = new HashSet<string>(_options.ValidAudiences, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(_options.ResourceApplicationId))
        {
            validAudiences.Add(_options.ResourceApplicationId);
        }

        var parameters = new TokenValidationParameters
        {
            ClockSkew = _options.ClockSkew,
            IssuerSigningKeys = snapshot.Configuration.SigningKeys.Where(
                key => EntraSigningKey.IsUsable(
                    key,
                    snapshot.Configuration,
                    _options,
                    document.Issuer,
                    document.TenantId)),
            LifetimeValidator = ValidateLifetime,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidAlgorithms = _options.AllowedAlgorithms,
            ValidAudiences = validAudiences,
            ValidIssuer = snapshot.Configuration.Issuer,
        };

        return _handler.ValidateTokenAsync(token, parameters);
    }

    private bool ValidateLifetime(
        DateTime? notBefore,
        DateTime? expires,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters)
    {
        if (notBefore is null || expires is null || expires <= notBefore)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return notBefore <= now + _options.ClockSkew
            && expires >= now - _options.ClockSkew;
    }

    private void ValidateUntrustedClaims(JwtDocument document, string clusterId)
    {
        if (!_options.SupportedTokenVersions.Contains(document.Version)
            || !_options.ValidTenantIds.Contains(document.TenantId)
            || document.ExpiresAt <= document.NotBefore
            || DateTimeOffset.FromUnixTimeSeconds(document.ExpiresAt)
                - DateTimeOffset.FromUnixTimeSeconds(document.NotBefore) > _options.MaximumTokenLifetime)
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.InvalidToken);
        }

        if (DateTimeOffset.FromUnixTimeSeconds(document.ExpiresAt) - _timeProvider.GetUtcNow()
            < _options.MinimumRemainingTokenLifetime)
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.ExpiredToken);
        }

        var isDelegated = document.Scopes.Count > 0;
        if (isDelegated && !_options.AllowDelegatedTokens)
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.UnauthorizedCaller);
        }

        if (!isDelegated && !string.Equals(document.IdentityType, ApplicationIdentityType, StringComparison.Ordinal))
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.UnauthorizedCaller);
        }

        var callerId = document.Version switch
        {
            "1.0" when document.AuthorizedParty is null && document.ApplicationId is not null => document.ApplicationId,
            "2.0" when document.ApplicationId is null && document.AuthorizedParty is not null => document.AuthorizedParty,
            _ => null,
        };

        if (callerId is null)
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.UnauthorizedCaller);
        }

        if (!_options.AllowAnyApplicationInTenant)
        {
            var hasCallerAllowlist = _options.AllowedClientIds.Count > 0
                || _options.AllowedServicePrincipalObjectIds.Count > 0;
            var callerIdAllowed = _options.AllowedClientIds.Contains(callerId);
            var objectIdAllowed = document.ObjectId is not null
                && _options.AllowedServicePrincipalObjectIds.Contains(document.ObjectId);
            if (hasCallerAllowlist && !callerIdAllowed && !objectIdAllowed)
            {
                throw new EntraAuthenticationException(EntraAuthenticationError.UnauthorizedCaller);
            }
        }

        if (_options.RequiredRoles.Count > 0 && !_options.RequiredRoles.Overlaps(document.Roles))
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.UnauthorizedCaller);
        }

        if (!string.IsNullOrWhiteSpace(_options.ClusterClaimType)
            && (!document.Claims.TryGetValue(_options.ClusterClaimType, out var clusterClaim)
                || !string.Equals(clusterClaim, clusterId, StringComparison.Ordinal)))
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.UnauthorizedCaller);
        }

        if (!string.IsNullOrWhiteSpace(_options.ClusterRoleFormat)
            && !document.Roles.Contains(
                JwtDocument.FormatClusterValue(_options.ClusterRoleFormat, clusterId),
                StringComparer.Ordinal))
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.UnauthorizedCaller);
        }

        if (!string.IsNullOrWhiteSpace(_options.ClusterRole)
            && !document.Roles.Contains(_options.ClusterRole, StringComparer.Ordinal))
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.UnauthorizedCaller);
        }

    }

    private void ValidateTrustedClaims(JwtDocument document, string issuer, string clusterId)
    {
        if (!string.Equals(document.Issuer, issuer, StringComparison.Ordinal)
            || !new Uri(issuer).AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Contains(document.TenantId, StringComparer.OrdinalIgnoreCase))
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.InvalidToken);
        }

        if (string.IsNullOrWhiteSpace(_options.ClusterClaimType)
            && string.IsNullOrWhiteSpace(_options.ClusterRole)
            && string.IsNullOrWhiteSpace(_options.ClusterRoleFormat))
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.UnauthorizedCaller);
        }

        ValidateUntrustedClaims(document, clusterId);
    }

    private static EntraAuthenticationError ClassifyValidationFailure(Exception? exception) => exception switch
    {
        SecurityTokenExpiredException => EntraAuthenticationError.ExpiredToken,
        SecurityTokenNotYetValidException => EntraAuthenticationError.ExpiredToken,
        SecurityTokenNoExpirationException => EntraAuthenticationError.ExpiredToken,
        SecurityTokenInvalidLifetimeException => EntraAuthenticationError.ExpiredToken,
        _ => EntraAuthenticationError.InvalidToken,
    };

    private sealed class JwtDocument
    {
        private JwtDocument(
            string algorithm,
            string keyId,
            Dictionary<string, string> claims,
            HashSet<string> audiences,
            HashSet<string> roles,
            HashSet<string> scopes,
            long notBefore,
            long expiresAt)
        {
            Algorithm = algorithm;
            KeyId = keyId;
            Claims = claims;
            Audiences = audiences;
            Roles = roles;
            Scopes = scopes;
            NotBefore = notBefore;
            ExpiresAt = expiresAt;
        }

        public string Algorithm { get; }

        public string KeyId { get; }

        public Dictionary<string, string> Claims { get; }

        public HashSet<string> Audiences { get; }

        public HashSet<string> Roles { get; }

        public HashSet<string> Scopes { get; }

        public long NotBefore { get; }

        public long ExpiresAt { get; }

        public string Issuer => GetRequired("iss");

        public string TenantId => GetRequired("tid");

        public string Version => GetRequired("ver");

        public string? AuthorizedParty => GetOptional("azp");

        public string? ApplicationId => GetOptional("appid");

        public string? ObjectId => GetOptional("oid");

        public string? IdentityType => GetOptional("idtyp");

        public static JwtDocument Parse(string token)
        {
            var segments = token.Split('.');
            if (segments.Length != 3)
            {
                throw new FormatException();
            }

            using var header = ParseObject(segments[0]);
            var payload = ParsePayload(segments[1]);
            var algorithm = ReadRequiredString(header, "alg");
            var keyId = ReadRequiredString(header, "kid");
            return new JwtDocument(
                algorithm,
                keyId,
                payload.Claims,
                payload.Audiences,
                payload.Roles,
                payload.Scopes,
                payload.NotBefore,
                payload.ExpiresAt);
        }

        public static string FormatClusterValue(string format, string clusterId)
            => string.Format(CultureInfo.InvariantCulture, format, clusterId);

        private static Payload ParsePayload(string segment)
        {
            using var document = ParseObject(segment);
            var claims = new Dictionary<string, string>(StringComparer.Ordinal);
            var audiences = new HashSet<string>(StringComparer.Ordinal);
            var roles = new HashSet<string>(StringComparer.Ordinal);
            var scopes = new HashSet<string>(StringComparer.Ordinal);
            long? notBefore = null;
            long? expiresAt = null;
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    throw new FormatException();
                }

                switch (property.Name)
                {
                    case "aud":
                        ReadStringSet(property.Value, audiences);
                        break;
                    case "roles":
                        ReadStringSet(property.Value, roles);
                        break;
                    case "scp":
                        var scopeValue = ReadString(property.Value);
                        foreach (var scope in scopeValue.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!scopes.Add(scope))
                            {
                                throw new FormatException();
                            }
                        }

                        claims.Add(property.Name, scopeValue);
                        break;
                    case "nbf":
                        notBefore = ReadUnixTime(property.Value);
                        break;
                    case "exp":
                        expiresAt = ReadUnixTime(property.Value);
                        break;
                    default:
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            claims.Add(property.Name, ReadString(property.Value));
                        }

                        break;
                }
            }

            if (audiences.Count == 0 || notBefore is null || expiresAt is null)
            {
                throw new FormatException();
            }

            var result = new Payload(claims, audiences, roles, scopes, notBefore.Value, expiresAt.Value);
            _ = GetRequiredClaim(result.Claims, "iss");
            _ = GetRequiredClaim(result.Claims, "tid");
            _ = GetRequiredClaim(result.Claims, "ver");
            return result;
        }

        private static JsonDocument ParseObject(string segment)
        {
            var bytes = Base64UrlEncoder.DecodeBytes(segment);
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new FormatException();
            }

            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    document.Dispose();
                    throw new FormatException();
                }
            }

            return document;
        }

        private static string ReadRequiredString(JsonDocument document, string propertyName)
        {
            if (!document.RootElement.TryGetProperty(propertyName, out var value))
            {
                throw new FormatException();
            }

            return ReadString(value);
        }

        private static string ReadString(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
            {
                throw new FormatException();
            }

            return value.GetString()!;
        }

        private static long ReadUnixTime(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
            {
                throw new FormatException();
            }

            _ = DateTimeOffset.FromUnixTimeSeconds(result);
            return result;
        }

        private static void ReadStringSet(JsonElement value, HashSet<string> destination)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                if (!destination.Add(ReadString(value)))
                {
                    throw new FormatException();
                }

                return;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException();
            }

            foreach (var element in value.EnumerateArray())
            {
                if (!destination.Add(ReadString(element)))
                {
                    throw new FormatException();
                }
            }
        }

        private string GetRequired(string name)
            => GetRequiredClaim(Claims, name);

        private string? GetOptional(string name)
            => Claims.TryGetValue(name, out var value) ? value : null;

        private static string GetRequiredClaim(Dictionary<string, string> claims, string name)
            => claims.TryGetValue(name, out var value) ? value : throw new FormatException();

        private readonly record struct Payload(
            Dictionary<string, string> Claims,
            HashSet<string> Audiences,
            HashSet<string> Roles,
            HashSet<string> Scopes,
            long NotBefore,
            long ExpiresAt);
    }
}
