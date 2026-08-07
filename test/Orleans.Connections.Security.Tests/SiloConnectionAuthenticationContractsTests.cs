using System.Security.Claims;
using Xunit;

namespace Orleans.Connections.Security.Tests;

public class SiloConnectionAuthenticationContractsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidationResult_Success_PreservesPrincipalExpirationAndInvariants(bool hasExpiration)
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "silo-17")], "test-token"));
        DateTimeOffset? expiration = hasExpiration
            ? new DateTimeOffset(2031, 4, 5, 6, 7, 8, TimeSpan.Zero)
            : null;

        var result = SiloConnectionTokenValidationResult.Success(principal, expiration);

        Assert.True(result.Succeeded);
        Assert.Same(principal, result.Principal);
        Assert.Equal(expiration, result.ExpiresAt);
        Assert.Equal(SiloConnectionAuthenticationFailure.None, result.Failure);
    }

    [Fact]
    public void ValidationResult_Success_NullPrincipal_Throws()
    {
        var expiration = new DateTimeOffset(2031, 4, 5, 6, 7, 8, TimeSpan.Zero);

        var exception = Assert.Throws<ArgumentNullException>(
            () => SiloConnectionTokenValidationResult.Success(null!, expiration));

        Assert.Equal("principal", exception.ParamName);
    }

    [Theory]
    [InlineData(SiloConnectionAuthenticationFailure.MissingToken)]
    [InlineData(SiloConnectionAuthenticationFailure.InvalidToken)]
    [InlineData(SiloConnectionAuthenticationFailure.ExpiredToken)]
    [InlineData(SiloConnectionAuthenticationFailure.UnauthorizedCaller)]
    [InlineData(SiloConnectionAuthenticationFailure.ProviderUnavailable)]
    [InlineData(SiloConnectionAuthenticationFailure.ValidationError)]
    public void ValidationResult_Fail_MapsEveryBoundedFailure(SiloConnectionAuthenticationFailure failure)
    {
        var result = SiloConnectionTokenValidationResult.Fail(failure);

        Assert.False(result.Succeeded);
        Assert.Null(result.Principal);
        Assert.Null(result.ExpiresAt);
        Assert.Equal(failure, result.Failure);
    }

    [Fact]
    public void ValidationResult_Fail_None_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => SiloConnectionTokenValidationResult.Fail(SiloConnectionAuthenticationFailure.None));

        Assert.Equal("failure", exception.ParamName);
    }

    [Fact]
    public void Token_Record_PreservesValueAndExpiration()
    {
        var expiration = new DateTimeOffset(2032, 8, 9, 10, 11, 12, TimeSpan.Zero);

        var finite = new SiloConnectionToken("finite-token-value", expiration);
        var nonExpiring = new SiloConnectionToken("non-expiring-token-value", null);

        Assert.Equal("finite-token-value", finite.Value);
        Assert.Equal(expiration, finite.ExpiresAt);
        Assert.Equal("non-expiring-token-value", nonExpiring.Value);
        Assert.Null(nonExpiring.ExpiresAt);
        Assert.NotEqual(finite, nonExpiring);
    }
}

public class SiloConnectionAuthenticationOptionsTests
{
    [Fact]
    public void Defaults_AreSecureAndBounded()
    {
        var options = new SiloConnectionAuthenticationOptions();

        Assert.Equal(SiloConnectionAuthenticationMode.Required, options.Mode);
        Assert.Equal(TimeSpan.FromSeconds(10), options.TokenExchangeTimeout);
        Assert.Equal(16 * 1024, options.MaxTokenSize);
        Assert.Equal(256, options.MaxConcurrentInboundAuthentications);
        Assert.Equal(256, options.MaxConcurrentOutboundAuthentications);
        Assert.Equal(256, options.MaxPendingInboundAuthentications);
        Assert.Equal(256, options.MaxPendingOutboundAuthentications);
        Assert.Equal(TimeSpan.FromMinutes(2), options.MinimumRemainingTokenLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ExpirationSafetyMargin);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ExpirationJitter);
        Assert.False(options.AllowNonExpiringCredentials);
        Assert.Null(options.TargetHost);
        Assert.Same(TimeProvider.System, options.TimeProvider);
    }

    [Fact]
    public void Properties_AreMutable()
    {
        var timeProvider = new TestTimeProvider();
        var options = new SiloConnectionAuthenticationOptions
        {
            Mode = SiloConnectionAuthenticationMode.Audit,
            TokenExchangeTimeout = TimeSpan.FromSeconds(17),
            MaxTokenSize = 32 * 1024,
            MaxConcurrentInboundAuthentications = 37,
            MaxConcurrentOutboundAuthentications = 41,
            MaxPendingInboundAuthentications = 43,
            MaxPendingOutboundAuthentications = 47,
            MinimumRemainingTokenLifetime = TimeSpan.FromMinutes(7),
            ExpirationSafetyMargin = TimeSpan.FromSeconds(53),
            ExpirationJitter = TimeSpan.FromSeconds(11),
            AllowNonExpiringCredentials = true,
            TargetHost = "silo.internal.example",
            TimeProvider = timeProvider,
        };

        Assert.Equal(SiloConnectionAuthenticationMode.Audit, options.Mode);
        Assert.Equal(TimeSpan.FromSeconds(17), options.TokenExchangeTimeout);
        Assert.Equal(32 * 1024, options.MaxTokenSize);
        Assert.Equal(37, options.MaxConcurrentInboundAuthentications);
        Assert.Equal(41, options.MaxConcurrentOutboundAuthentications);
        Assert.Equal(43, options.MaxPendingInboundAuthentications);
        Assert.Equal(47, options.MaxPendingOutboundAuthentications);
        Assert.Equal(TimeSpan.FromMinutes(7), options.MinimumRemainingTokenLifetime);
        Assert.Equal(TimeSpan.FromSeconds(53), options.ExpirationSafetyMargin);
        Assert.Equal(TimeSpan.FromSeconds(11), options.ExpirationJitter);
        Assert.True(options.AllowNonExpiringCredentials);
        Assert.Equal("silo.internal.example", options.TargetHost);
        Assert.Same(timeProvider, options.TimeProvider);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
    }
}

public class SiloConnectionAuthenticationProtocolTests
{
    [Fact]
    public void Version2_IsExpectedAlpnIdentifier()
    {
        Assert.Equal(
            "Orleans1+TokenAuth2",
            SiloConnectionAuthenticationProtocol.Version2,
            StringComparer.Ordinal);
    }
}
