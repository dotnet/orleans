using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using Orleans.Configuration;
using Orleans.Connections.Security.Entra;

namespace Orleans.Connections.Security.Entra.Tests;

internal sealed class EntraTestFixture : IDisposable
{
    public const string Audience = "44444444-4444-4444-4444-444444444444";
    public const string ClientId = "11111111-1111-1111-1111-111111111111";
    public const string ClusterId = "cluster-a";
    public const string Issuer = "https://login.microsoftonline.com/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0";
    public const string ObjectId = "22222222-2222-2222-2222-222222222222";
    public const string Role = "Orleans.Silo.Connect";
    public const string TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private readonly List<RSA> _keys = [];

    public EntraTestFixture()
    {
        TimeProvider = new TestTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        Options = CreateOptions();
        Metadata = new TestDocumentRetriever(Options.Authority!);
        CurrentKey = CreateKey("key-1");
        Metadata.SetConfiguration(Issuer, CurrentKey);
    }

    public SigningCredentials CurrentKey { get; private set; }

    public TestDocumentRetriever Metadata { get; }

    public EntraSiloConnectionOptions Options { get; }

    public TestTimeProvider TimeProvider { get; }

    public static EntraSiloConnectionOptions CreateOptions(
        string authority = Issuer,
        string audience = Audience)
    {
        var options = new EntraSiloConnectionOptions
        {
            Authority = new Uri(authority),
            TokenScope = $"api://11111111-1111-1111-1111-111111111111/{ClusterId}",
            ResourceApplicationId = audience,
            ClusterClaimType = "orleans_cluster",
            MetadataRefreshJitterRatio = 0,
        };
        options.ValidAudiences.Add(audience);
        options.ValidTenantIds.Add(TenantId);
        options.AllowedClientIds.Add(ClientId);
        options.RequiredRoles.Add(Role);
        return options;
    }

    public SigningCredentials CreateKey(string keyId, string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var rsa = RSA.Create(2048);
        _keys.Add(rsa);
        return new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = keyId }, algorithm);
    }

    public EntraJwtValidator CreateValidator()
    {
        var provider = new EntraOpenIdConfigurationProvider(Options, Metadata, TimeProvider, static () => 0);
        return new EntraJwtValidator(Options, provider, TimeProvider);
    }

    public string CreateToken(
        string version = "2.0",
        SigningCredentials? signingCredentials = null,
        string issuer = Issuer,
        string tenantId = TenantId,
        string audience = Audience,
        string clusterId = ClusterId,
        string clientId = ClientId,
        string objectId = ObjectId,
        string identityType = "app",
        string[]? roles = null,
        string? scopes = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires = null,
        IDictionary<string, object>? additionalClaims = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["tid"] = tenantId,
            ["ver"] = version,
            ["oid"] = objectId,
            ["idtyp"] = identityType,
            ["orleans_cluster"] = clusterId,
            ["roles"] = roles ?? [Role],
        };
        claims[version == "1.0" ? "appid" : "azp"] = clientId;
        if (scopes is not null)
        {
            claims["scp"] = scopes;
        }

        if (additionalClaims is not null)
        {
            foreach (var pair in additionalClaims)
            {
                claims[pair.Key] = pair.Value;
            }
        }

        var now = TimeProvider.GetUtcNow();
        var descriptor = new SecurityTokenDescriptor
        {
            Audience = audience,
            Claims = claims,
            Expires = (expires ?? now.AddMinutes(30)).UtcDateTime,
            Issuer = issuer,
            NotBefore = (notBefore ?? now.AddMinutes(-1)).UtcDateTime,
            SigningCredentials = signingCredentials ?? CurrentKey,
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public void RollMetadataTo(SigningCredentials key, string issuer = Issuer, string use = "sig", string[]? keyOperations = null)
    {
        CurrentKey = key;
        Metadata.SetConfiguration(issuer, key, use, keyOperations);
    }

    public void Dispose()
    {
        foreach (var key in _keys)
        {
            key.Dispose();
        }
    }

    public static string CreateDuplicateClaimToken()
    {
        const string header = """{"alg":"RS256","kid":"key-1"}""";
        const string payload = """{"iss":"x","tid":"x","tid":"y","ver":"2.0","nbf":1,"exp":2,"aud":"x"}""";
        return $"{Base64UrlEncoder.Encode(header)}.{Base64UrlEncoder.Encode(payload)}.invalid";
    }

    public static string CreateMalformedToken(string payload)
    {
        const string header = """{"alg":"RS256","kid":"key-1"}""";
        return $"{Base64UrlEncoder.Encode(header)}.{Base64UrlEncoder.Encode(payload)}.invalid";
    }
}

internal sealed class TestDocumentRetriever : IDocumentRetriever
{
    private readonly Uri _authority;
    private readonly ConcurrentDictionary<string, string> _documents = new(StringComparer.Ordinal);
    private int _requestCount;

    public TestDocumentRetriever(Uri authority)
    {
        _authority = authority;
    }

    public bool FailRequests { get; set; }

    public TimeSpan ResponseDelay { get; set; }

    public int RequestCount => Volatile.Read(ref _requestCount);

    public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        Interlocked.Increment(ref _requestCount);
        if (FailRequests)
        {
            throw new InvalidOperationException("simulated outage");
        }

        return GetCoreAsync(address, cancel);
    }

    public void SetConfiguration(
        string issuer,
        SigningCredentials signingCredentials,
        string use = "sig",
        string[]? keyOperations = null,
        string? jwksUri = null)
    {
        var authority = _authority.AbsoluteUri.TrimEnd('/');
        var keysAddress = jwksUri ?? $"{authority}/keys";
        _documents[$"{authority}/.well-known/openid-configuration"] =
            $$"""{"issuer":"{{issuer}}","jwks_uri":"{{keysAddress}}"}""";
        _documents[keysAddress] = CreateJwks(signingCredentials, use, keyOperations);
    }

    private async Task<string> GetCoreAsync(string address, CancellationToken cancellationToken)
    {
        if (ResponseDelay > TimeSpan.Zero)
        {
            await Task.Delay(ResponseDelay, cancellationToken);
        }

        return _documents.TryGetValue(address, out var document)
            ? document
            : throw new InvalidOperationException("unknown metadata address");
    }

    private static string CreateJwks(
        SigningCredentials signingCredentials,
        string use,
        string[]? keyOperations)
    {
        var key = (RsaSecurityKey)signingCredentials.Key;
        var parameters = key.Rsa?.ExportParameters(includePrivateParameters: false) ?? key.Parameters;
        var operations = keyOperations is null
            ? string.Empty
            : $$""","key_ops":{{System.Text.Json.JsonSerializer.Serialize(keyOperations)}}""";
        return $$"""
            {"keys":[{"kty":"RSA","use":"{{use}}","kid":"{{key.KeyId}}","alg":"{{signingCredentials.Algorithm}}","n":"{{Base64UrlEncoder.Encode(parameters.Modulus)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent)}}"{{operations}}}]}
            """;
    }
}

internal sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan amount) => UtcNow += amount;
}

internal sealed class TestTokenCredential(Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> getToken)
    : TokenCredential
{
    public int CallCount { get; private set; }

    public TokenRequestContext? LastRequestContext { get; private set; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestContext = requestContext;
        return getToken(requestContext, cancellationToken);
    }
}

internal sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(send(request));
    }
}
