using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Orleans.Connections.Security.Entra;

namespace Orleans.Connections.Security.Entra.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Security")]
public sealed class EntraJwtValidatorTests
{
    [Theory]
    [InlineData("1.0")]
    [InlineData("2.0")]
    public async Task AcceptsValidApplicationToken(string version)
    {
        using var fixture = new EntraTestFixture();
        var validator = fixture.CreateValidator();
        var token = fixture.CreateToken(version);

        var result = await validator.ValidateAsync(token, EntraTestFixture.ClusterId, CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
        Assert.Equal(EntraTestFixture.ClientId, result.Principal.FindFirst(version == "1.0" ? "appid" : "azp")?.Value);
        Assert.Equal(fixture.TimeProvider.GetUtcNow().AddMinutes(30), result.ExpiresAt);
    }

    [Fact]
    public async Task AcceptsRealisticV1IssuerWhenExplicitlyTrusted()
    {
        const string authority = "https://login.microsoftonline.com/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string issuer = "https://sts.windows.net/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/";
        using var fixture = new EntraTestFixture();
        var options = EntraTestFixture.CreateOptions(authority);
        options.AdditionalTrustedMetadataHosts.Add("sts.windows.net");
        var metadata = new TestDocumentRetriever(options.Authority!);
        metadata.SetConfiguration(issuer, fixture.CurrentKey);
        using var provider = new EntraOpenIdConfigurationProvider(options, metadata, fixture.TimeProvider, static () => 0);
        var validator = new EntraJwtValidator(options, provider, fixture.TimeProvider);
        var token = fixture.CreateToken(version: "1.0", issuer: issuer);

        var result = await validator.ValidateAsync(token, EntraTestFixture.ClusterId, CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task RejectsExpiredToken()
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(
            notBefore: fixture.TimeProvider.GetUtcNow().AddMinutes(-30),
            expires: fixture.TimeProvider.GetUtcNow().AddMinutes(-5));

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.ExpiredToken);
    }

    [Fact]
    public async Task AcceptsNotBeforeWithinClockSkew()
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(notBefore: fixture.TimeProvider.GetUtcNow().AddMinutes(1));

        var result = await fixture.CreateValidator().ValidateAsync(
            token,
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task RejectsNotBeforeOutsideClockSkew()
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(notBefore: fixture.TimeProvider.GetUtcNow().AddMinutes(3));

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.ExpiredToken);
    }

    [Fact]
    public async Task RejectsExcessiveTokenLifetime()
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(
            notBefore: fixture.TimeProvider.GetUtcNow().AddMinutes(-1),
            expires: fixture.TimeProvider.GetUtcNow().AddHours(3));

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/v2.0", EntraTestFixture.TenantId, EntraTestFixture.Audience)]
    [InlineData(EntraTestFixture.Issuer, "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", EntraTestFixture.Audience)]
    [InlineData(EntraTestFixture.Issuer, EntraTestFixture.TenantId, "orleans-silos")]
    public async Task RejectsWrongIssuerTenantOrExactAudience(string issuer, string tenant, string audience)
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(issuer: issuer, tenantId: tenant, audience: audience);

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Fact]
    public async Task RejectsWrongCluster()
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(clusterId: "cluster-b");

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.UnauthorizedCaller);
    }

    [Fact]
    public async Task SupportsClusterSpecificRoleBinding()
    {
        using var fixture = new EntraTestFixture();
        fixture.Options.ClusterClaimType = null;
        fixture.Options.ClusterRoleFormat = "Orleans.Silo.Connect.{0}";
        var token = fixture.CreateToken(roles: [EntraTestFixture.Role, "Orleans.Silo.Connect.cluster-a"]);

        var result = await fixture.CreateValidator().ValidateAsync(
            token,
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
    }

    [Theory]
    [InlineData("legacy-format", "InvalidToken")]
    [InlineData("token-scope", "InvalidToken")]
    [InlineData("resource-application-id", "UnauthorizedCaller")]
    public async Task RejectsLegacyClusterAudienceBinding(
        string audienceSource,
        string expectedError)
    {
        using var fixture = new EntraTestFixture();
        fixture.Options.ClusterClaimType = null;
#pragma warning disable CS0618
        fixture.Options.ClusterAudienceFormat = "api://orleans-silos/{0}";
#pragma warning restore CS0618
        var audience = audienceSource switch
        {
            "legacy-format" => "api://orleans-silos/cluster-a",
            "token-scope" => fixture.Options.TokenScope!,
            "resource-application-id" => fixture.Options.ResourceApplicationId!,
            _ => throw new ArgumentOutOfRangeException(nameof(audienceSource)),
        };
        var token = fixture.CreateToken(audience: audience);

        await AssertErrorAsync(fixture, token, Enum.Parse<EntraAuthenticationError>(expectedError));
    }

    [Fact]
    public async Task RejectsDelegatedTokenByDefault()
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(scopes: "user.read");

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.UnauthorizedCaller);
    }

    [Fact]
    public async Task AllowsDelegatedTokenOnlyWhenExplicitlyConfigured()
    {
        using var fixture = new EntraTestFixture();
        fixture.Options.AllowDelegatedTokens = true;
        var token = fixture.CreateToken(identityType: "user", scopes: "user.read");

        var result = await fixture.CreateValidator().ValidateAsync(
            token,
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
    }

    [Theory]
    [InlineData("33333333-3333-3333-3333-333333333333", EntraTestFixture.Role)]
    [InlineData(EntraTestFixture.ClientId, "Wrong.Role")]
    public async Task RejectsWrongCallerOrApplicationRole(string clientId, string role)
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(clientId: clientId, roles: [role]);

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.UnauthorizedCaller);
    }

    [Fact]
    public async Task AuthorizesConfiguredServicePrincipalObjectId()
    {
        using var fixture = new EntraTestFixture();
        fixture.Options.AllowedClientIds.Clear();
        fixture.Options.AllowedServicePrincipalObjectIds.Add(EntraTestFixture.ObjectId);
        var token = fixture.CreateToken();

        var result = await fixture.CreateValidator().ValidateAsync(
            token,
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task RejectsWrongServicePrincipalObjectId()
    {
        using var fixture = new EntraTestFixture();
        fixture.Options.AllowedClientIds.Clear();
        fixture.Options.AllowedServicePrincipalObjectIds.Add("33333333-3333-3333-3333-333333333333");
        var token = fixture.CreateToken();

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.UnauthorizedCaller);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public async Task CombinedCallerAllowlists_AuthorizeWhenEitherIdentityMatches(
        bool clientIdMatches,
        bool objectIdMatches,
        bool succeeds)
    {
        const string otherClientId = "33333333-3333-3333-3333-333333333333";
        const string otherObjectId = "44444444-4444-4444-4444-444444444444";
        using var fixture = new EntraTestFixture();
        fixture.Options.AllowedClientIds.Clear();
        fixture.Options.AllowedClientIds.Add(clientIdMatches ? EntraTestFixture.ClientId : otherClientId);
        fixture.Options.AllowedServicePrincipalObjectIds.Add(
            objectIdMatches ? EntraTestFixture.ObjectId : otherObjectId);
        var token = fixture.CreateToken();

        if (succeeds)
        {
            var result = await fixture.CreateValidator().ValidateAsync(
                token,
                EntraTestFixture.ClusterId,
                CancellationToken.None);

            Assert.True(result.Principal.Identity?.IsAuthenticated);
            Assert.Equal(EntraTestFixture.ClientId, result.Principal.FindFirst("azp")?.Value);
            Assert.Equal(EntraTestFixture.ObjectId, result.Principal.FindFirst("oid")?.Value);
        }
        else
        {
            await AssertErrorAsync(fixture, token, EntraAuthenticationError.UnauthorizedCaller);
        }
    }

    [Theory]
    [InlineData("1.0", "azp")]
    [InlineData("2.0", "appid")]
    public async Task RejectsAmbiguousCallerIdentity(string version, string conflictingClaim)
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(
            version: version,
            additionalClaims: new Dictionary<string, object> { [conflictingClaim] = EntraTestFixture.ClientId });

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.UnauthorizedCaller);
    }

    [Fact]
    public async Task RejectsDuplicateIdentityClaim()
    {
        using var fixture = new EntraTestFixture();
        var token = EntraTestFixture.CreateDuplicateClaimToken();

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Fact]
    public async Task RejectsDuplicateAuthorizationClaim()
    {
        using var fixture = new EntraTestFixture();
        var token = EntraTestFixture.CreateMalformedToken(
            """{"iss":"x","tid":"x","ver":"2.0","azp":"x","idtyp":"app","roles":["a"],"roles":["b"],"nbf":1,"exp":2,"aud":"x"}""");

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Theory]
    [InlineData("""{"iss":"x","tid":"x","ver":"2.0","azp":"x","idtyp":"app","roles":["a"],"exp":2,"aud":"x"}""")]
    [InlineData("""{"iss":"x","tid":"x","ver":"2.0","azp":"x","idtyp":"app","roles":["a"],"nbf":1,"aud":"x"}""")]
    public async Task RejectsTokenWithoutFiniteLifetime(string payload)
    {
        using var fixture = new EntraTestFixture();
        var token = EntraTestFixture.CreateMalformedToken(payload);

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Fact]
    public async Task RejectsUnsupportedTokenVersion()
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(version: "3.0");

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Fact]
    public async Task RejectsUnsignedToken()
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(signingCredentials: null);
        var segments = token.Split('.');
        var unsignedHeader = Base64UrlEncoder.Encode("""{"alg":"none","kid":"key-1"}""");
        token = $"{unsignedHeader}.{segments[1]}.";

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Fact]
    public async Task RejectsSymmetricAlgorithmEvenWhenAddedToAllowlist()
    {
        using var fixture = new EntraTestFixture();
        fixture.Options.AllowedAlgorithms.Add(SecurityAlgorithms.HmacSha256);
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "symmetric" };
        var token = fixture.CreateToken(signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Fact]
    public async Task RejectsDisallowedAsymmetricAlgorithm()
    {
        using var fixture = new EntraTestFixture();
        var key = fixture.CreateKey("rsa-384", SecurityAlgorithms.RsaSha384);
        var token = fixture.CreateToken(signingCredentials: key);

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Fact]
    public async Task RejectsMismatchedSigningKeyIssuer()
    {
        using var fixture = new EntraTestFixture();
        fixture.Metadata.SetConfiguration(
            EntraTestFixture.Issuer,
            fixture.CurrentKey,
            keyIssuer: "https://login.microsoftonline.com/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/v2.0");
        var token = fixture.CreateToken();

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Fact]
    public async Task RejectsMismatchedIssuerForDuplicateSigningKeyId()
    {
        using var fixture = new EntraTestFixture();
        var untrustedKey = fixture.CreateKey(fixture.CurrentKey.Key.KeyId);
        fixture.Metadata.SetConfigurationWithDuplicateKeyId(
            EntraTestFixture.Issuer,
            fixture.CurrentKey,
            untrustedKey,
            "https://login.microsoftonline.com/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/v2.0");
        var token = fixture.CreateToken(signingCredentials: untrustedKey);

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Fact]
    public async Task AcceptsTemplatedSigningKeyIssuer()
    {
        using var fixture = new EntraTestFixture();
        const string templatedIssuer = "https://login.microsoftonline.com/{tenantid}/v2.0";
        fixture.Metadata.SetConfiguration(
            EntraTestFixture.Issuer,
            fixture.CurrentKey,
            keyIssuer: templatedIssuer);
        var token = fixture.CreateToken();

        var result = await fixture.CreateValidator().ValidateAsync(
            token,
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task RejectsMismatchedSigningKeyCloudInstance()
    {
        using var fixture = new EntraTestFixture();
        fixture.Metadata.SetConfiguration(
            EntraTestFixture.Issuer,
            fixture.CurrentKey,
            keyCloudInstanceName: "microsoftonline.us",
            configurationCloudInstanceName: "microsoftonline.com");
        var token = fixture.CreateToken();

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Fact]
    public async Task AcceptsMatchingSigningKeyCloudInstance()
    {
        using var fixture = new EntraTestFixture();
        fixture.Metadata.SetConfiguration(
            EntraTestFixture.Issuer,
            fixture.CurrentKey,
            keyCloudInstanceName: "microsoftonline.com",
            configurationCloudInstanceName: "microsoftonline.com");
        var token = fixture.CreateToken();

        var result = await fixture.CreateValidator().ValidateAsync(
            token,
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task AcceptsX5cOnlySigningKey()
    {
        using var fixture = new EntraTestFixture();
        var signingCredentials = fixture.CreateCertificateKey("x5c-key");
        fixture.Metadata.SetConfiguration(EntraTestFixture.Issuer, signingCredentials);
        var token = fixture.CreateToken(signingCredentials: signingCredentials);

        var result = await fixture.CreateValidator().ValidateAsync(
            token,
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task NeverIncludesTokenInFailure()
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(clientId: "not-authorized");
        var validator = fixture.CreateValidator();

        var exception = await Assert.ThrowsAsync<EntraAuthenticationException>(
            () => validator.ValidateAsync(token, EntraTestFixture.ClusterId, CancellationToken.None).AsTask());

        Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
    }

    private static async Task AssertErrorAsync(
        EntraTestFixture fixture,
        string token,
        EntraAuthenticationError expected)
    {
        var exception = await Assert.ThrowsAsync<EntraAuthenticationException>(
            () => fixture.CreateValidator()
                .ValidateAsync(token, EntraTestFixture.ClusterId, CancellationToken.None)
                .AsTask());
        Assert.Equal(expected, exception.Error);
    }

    [Fact]
    public async Task AcceptsV2TokenWithGuidAudienceAndExactClusterRole()
    {
        using var fixture = new EntraTestFixture();
        ConfigureExactClusterRole(fixture);
        fixture.Options.ValidAudiences.Clear();
        var token = fixture.CreateToken(roles: [EntraTestFixture.Role, ExactClusterRole]);

        var result = await fixture.CreateValidator().ValidateAsync(
            token,
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        var identity = Assert.IsType<System.Security.Claims.ClaimsIdentity>(result.Principal.Identity);
        Assert.True(identity.IsAuthenticated);
        Assert.Equal("Entra", identity.AuthenticationType);
        Assert.Equal(EntraTestFixture.ClientId, result.Principal.FindFirst("azp")?.Value);
        Assert.Equal(EntraTestFixture.Audience, result.Principal.FindFirst("aud")?.Value);
        Assert.Contains(result.Principal.FindAll("roles"), claim => claim.Value == ExactClusterRole);
        Assert.Equal(fixture.TimeProvider.GetUtcNow().AddMinutes(30), result.ExpiresAt);
    }

    [Fact]
    public async Task RejectsV2TokenWithUriAudience()
    {
        using var fixture = new EntraTestFixture();
        ConfigureExactClusterRole(fixture);
        fixture.Options.ValidAudiences.Clear();
#pragma warning disable CS0618
        fixture.Options.ClusterAudienceFormat = "api://11111111-1111-1111-1111-111111111111/{0}";
#pragma warning restore CS0618
        var token = fixture.CreateToken(
            audience: fixture.Options.TokenScope!,
            roles: [EntraTestFixture.Role, ExactClusterRole]);

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unrelated")]
    [InlineData("prefix")]
    [InlineData("other-cluster")]
    [InlineData("case-mismatch")]
    public async Task RejectsMissingOrWrongClusterRole(string roleCase)
    {
        using var fixture = new EntraTestFixture();
        ConfigureExactClusterRole(fixture);
        string[] roles = roleCase switch
        {
            "missing" => [EntraTestFixture.Role],
            "unrelated" => [EntraTestFixture.Role, "Unrelated.Role"],
            "prefix" => [EntraTestFixture.Role, "Orleans.Silo.Connect.cluster"],
            "other-cluster" => [EntraTestFixture.Role, "Orleans.Silo.Connect.cluster-b"],
            "case-mismatch" => [EntraTestFixture.Role, "orleans.silo.connect.cluster-a"],
            _ => throw new ArgumentOutOfRangeException(nameof(roleCase)),
        };
        var token = fixture.CreateToken(roles: roles);

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.UnauthorizedCaller);
    }

    [Fact]
    public async Task AcceptsMultipleRolesIncludingExactClusterRole()
    {
        using var fixture = new EntraTestFixture();
        ConfigureExactClusterRole(fixture);
        var token = fixture.CreateToken(
            roles: [EntraTestFixture.Role, "Unrelated.Before", ExactClusterRole, "Unrelated.After"]);

        var result = await fixture.CreateValidator().ValidateAsync(
            token,
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
        Assert.Equal(
            [EntraTestFixture.Role, "Unrelated.Before", ExactClusterRole, "Unrelated.After"],
            result.Principal.FindAll("roles").Select(claim => claim.Value));
        Assert.Equal(fixture.TimeProvider.GetUtcNow().AddMinutes(30), result.ExpiresAt);
    }

    [Theory]
    [InlineData("wrong-tenant")]
    [InlineData("issuer-mismatch")]
    public async Task RejectsWrongTenantOrIssuer(string failureCase)
    {
        using var fixture = new EntraTestFixture();
        ConfigureExactClusterRole(fixture);
        var token = failureCase switch
        {
            "wrong-tenant" => fixture.CreateToken(
                tenantId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                roles: [EntraTestFixture.Role, ExactClusterRole]),
            "issuer-mismatch" => fixture.CreateToken(
                issuer: "https://login.microsoftonline.com/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/v2.0",
                roles: [EntraTestFixture.Role, ExactClusterRole]),
            _ => throw new ArgumentOutOfRangeException(nameof(failureCase)),
        };

        await AssertErrorAsync(fixture, token, EntraAuthenticationError.InvalidToken);
    }

    [Theory]
    [InlineData(EntraTestFixture.ClusterId, true)]
    [InlineData("Cluster-A", false)]
    [InlineData("cluster", false)]
    [InlineData("cluster-a-suffix", false)]
    public async Task ConfiguredCustomClusterClaimRequiresExactOrdinalValue(string claimValue, bool succeeds)
    {
        using var fixture = new EntraTestFixture();
        var token = fixture.CreateToken(clusterId: claimValue);

        if (succeeds)
        {
            var result = await fixture.CreateValidator().ValidateAsync(
                token,
                EntraTestFixture.ClusterId,
                CancellationToken.None);

            Assert.True(result.Principal.Identity?.IsAuthenticated);
            Assert.Equal(claimValue, result.Principal.FindFirst("orleans_cluster")?.Value);
            Assert.Equal(fixture.TimeProvider.GetUtcNow().AddMinutes(30), result.ExpiresAt);
        }
        else
        {
            await AssertErrorAsync(fixture, token, EntraAuthenticationError.UnauthorizedCaller);
        }
    }

    private const string ExactClusterRole = "Orleans.Silo.Connect.cluster-a";

    private static void ConfigureExactClusterRole(EntraTestFixture fixture)
    {
        fixture.Options.ClusterClaimType = null;
        fixture.Options.ClusterRole = ExactClusterRole;
    }
}
