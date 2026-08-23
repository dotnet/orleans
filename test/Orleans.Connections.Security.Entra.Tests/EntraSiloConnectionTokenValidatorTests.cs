using System.Reflection;
using Orleans.Connections.Security.Entra;

namespace Orleans.Connections.Security.Entra.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Security")]
public sealed class EntraSiloConnectionTokenValidatorTests
{
    private const string ExactClusterRole = "Orleans.Silo.Connect.cluster-a";

    [Theory]
    [InlineData("invalid-audience", SiloConnectionAuthenticationFailure.InvalidToken)]
    [InlineData("missing-role", SiloConnectionAuthenticationFailure.UnauthorizedCaller)]
    [InlineData("wrong-role", SiloConnectionAuthenticationFailure.UnauthorizedCaller)]
    [InlineData("wrong-tenant", SiloConnectionAuthenticationFailure.InvalidToken)]
    [InlineData("issuer-mismatch", SiloConnectionAuthenticationFailure.InvalidToken)]
    [InlineData("expired", SiloConnectionAuthenticationFailure.ExpiredToken)]
    public async Task ValidateTokenAsync_MapsJwtFailureToBoundedCategory(
        string failureCase,
        SiloConnectionAuthenticationFailure expectedFailure)
    {
        using var fixture = new EntraTestFixture();
        ConfigureExactClusterRole(fixture);
        var token = failureCase switch
        {
            "invalid-audience" => fixture.CreateToken(
                audience: fixture.Options.TokenScope!,
                roles: [EntraTestFixture.Role, ExactClusterRole]),
            "missing-role" => fixture.CreateToken(roles: [EntraTestFixture.Role]),
            "wrong-role" => fixture.CreateToken(
                roles: [EntraTestFixture.Role, "Orleans.Silo.Connect.cluster-b"]),
            "wrong-tenant" => fixture.CreateToken(
                tenantId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                roles: [EntraTestFixture.Role, ExactClusterRole]),
            "issuer-mismatch" => fixture.CreateToken(
                issuer: "https://login.microsoftonline.com/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/v2.0",
                roles: [EntraTestFixture.Role, ExactClusterRole]),
            "expired" => fixture.CreateToken(
                roles: [EntraTestFixture.Role, ExactClusterRole],
                notBefore: fixture.TimeProvider.GetUtcNow().AddMinutes(-30),
                expires: fixture.TimeProvider.GetUtcNow().AddMinutes(-5)),
            _ => throw new ArgumentOutOfRangeException(nameof(failureCase)),
        };
        using var validator = new EntraSiloConnectionTokenValidator(fixture.CreateValidator());

        var result = await validator.ValidateTokenAsync(token, CreateContext(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedFailure, result.Failure);
        Assert.Null(result.Principal);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task ValidateTokenAsync_DoesNotExposeTokenOrClaimValuesInFailure()
    {
        const string tenant = "tenant-secret";
        const string issuer = "https://issuer-secret.example/v2.0";
        const string audience = "audience-secret";
        const string role = "role-secret";
        using var fixture = new EntraTestFixture();
        ConfigureExactClusterRole(fixture);
        var token = fixture.CreateToken(
            tenantId: tenant,
            issuer: issuer,
            audience: audience,
            roles: [role]);
        using var validator = new EntraSiloConnectionTokenValidator(fixture.CreateValidator());

        var result = await validator.ValidateTokenAsync(token, CreateContext(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SiloConnectionAuthenticationFailure.InvalidToken, result.Failure);
        Assert.Null(result.Principal);
        Assert.Null(result.ExpiresAt);
        var publicDiagnostic = $"{result.Succeeded}|{result.Failure}|{result.Principal}|{result.ExpiresAt}|{result}";
        Assert.DoesNotContain(token, publicDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(tenant, publicDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(issuer, publicDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(audience, publicDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(role, publicDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateTokenAsync_ReturnsPrincipalAndExpirationOnSuccess()
    {
        using var fixture = new EntraTestFixture();
        ConfigureExactClusterRole(fixture);
        fixture.Options.ValidAudiences.Clear();
        var token = fixture.CreateToken(
            roles: [EntraTestFixture.Role, "Unrelated.Before", ExactClusterRole, "Unrelated.After"]);
        using var validator = new EntraSiloConnectionTokenValidator(fixture.CreateValidator());

        var result = await validator.ValidateTokenAsync(token, CreateContext(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(SiloConnectionAuthenticationFailure.None, result.Failure);
        var principal = Assert.IsType<System.Security.Claims.ClaimsPrincipal>(result.Principal);
        Assert.True(principal.Identity?.IsAuthenticated);
        Assert.Equal("Entra", principal.Identity?.AuthenticationType);
        Assert.Equal(EntraTestFixture.ClientId, principal.FindFirst("azp")?.Value);
        Assert.Equal(
            [EntraTestFixture.Role, "Unrelated.Before", ExactClusterRole, "Unrelated.After"],
            principal.FindAll("roles").Select(claim => claim.Value));
        Assert.Equal(fixture.TimeProvider.GetUtcNow().AddMinutes(30), result.ExpiresAt);
    }

    private static void ConfigureExactClusterRole(EntraTestFixture fixture)
    {
        fixture.Options.ClusterClaimType = null;
        fixture.Options.ClusterRole = ExactClusterRole;
    }

    private static SiloConnectionTokenValidationContext CreateContext()
        => (SiloConnectionTokenValidationContext)Activator.CreateInstance(
            typeof(SiloConnectionTokenValidationContext),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [EntraTestFixture.ClusterId, SiloConnectionAuthenticationTarget.Silo, null, null],
            culture: null)!;
}
