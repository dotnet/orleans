using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Connections.Security.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Security")]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Token_ToString_RedactsValue(bool hasExpiration)
    {
        const string tokenValue = "secret-bearer-token";
        DateTimeOffset? expiration = hasExpiration
            ? new DateTimeOffset(2032, 8, 9, 10, 11, 12, TimeSpan.Zero)
            : null;
        var token = new SiloConnectionToken(tokenValue, expiration);

        var result = token.ToString();

        Assert.DoesNotContain(tokenValue, result, StringComparison.Ordinal);
        Assert.Equal(
            hasExpiration
                ? "SiloConnectionToken { Value = [REDACTED], ExpiresAt = 2032-08-09T10:11:12.0000000+00:00 }"
                : "SiloConnectionToken { Value = [REDACTED], ExpiresAt = null }",
            result);
    }
}

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Security")]
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

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Security")]
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

    [Theory]
    [InlineData(SiloConnectionAuthenticationMode.Disabled, "Disabled")]
    [InlineData(SiloConnectionAuthenticationMode.Audit, "Audit")]
    [InlineData(SiloConnectionAuthenticationMode.Required, "Required")]
    [InlineData((SiloConnectionAuthenticationMode)int.MaxValue, "Unknown")]
    public void TelemetryModeName_ReturnsBoundedConstants(
        SiloConnectionAuthenticationMode mode,
        string expected)
    {
        var actual = SiloConnectionAuthenticationTelemetry.GetModeName(mode);

        Assert.Equal(expected, actual);
        Assert.Same(actual, SiloConnectionAuthenticationTelemetry.GetModeName(mode));
    }

    [TestCategory("BVT")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Security")]
    public class SiloConnectionAuthenticationContextTests
    {
        [Theory]
        [InlineData(SiloConnectionAuthenticationTarget.Silo)]
        [InlineData(SiloConnectionAuthenticationTarget.Client)]
        public void Contexts_PreserveConnectionTarget(SiloConnectionAuthenticationTarget target)
        {
            var request = new SiloConnectionTokenRequestContext("cluster", target, null, null);
            var validation = new SiloConnectionTokenValidationContext("cluster", target, null, null);

            Assert.Equal(target, request.Target);
            Assert.Equal(target, validation.Target);
        }

        [TestCategory("BVT")]
        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Security")]
        public class SiloConnectionAuthenticationRegistrationTests
        {
            [Fact]
            public void Providers_AreIsolatedByConnectionPath()
            {
                var services = new ServiceCollection();
                var siloKey = ConnectionAuthenticationServiceKeys.Silo;
                var clientKey = new object();
                var siloProvider = new TestTokenProvider("silo");
                var clientProvider = new TestTokenProvider("client");

                new SiloConnectionAuthenticationBuilder(
                        "silo",
                        siloKey,
                        new SiloConnectionAuthenticationOptions(),
                        services)
                    .UseTokenProvider(siloProvider);
                new SiloConnectionAuthenticationBuilder(
                        "client",
                        clientKey,
                        new SiloConnectionAuthenticationOptions(),
                        services)
                    .UseTokenProvider(clientProvider);

                using var serviceProvider = services.BuildServiceProvider();
                Assert.Same(clientProvider, serviceProvider.GetRequiredKeyedService<ISiloConnectionTokenProvider>(clientKey));
                Assert.Same(siloProvider, serviceProvider.GetRequiredService<ISiloConnectionTokenProvider>());
                Assert.Null(serviceProvider.GetKeyedService<ISiloConnectionTokenProvider>(siloKey));
            }

            [Fact]
            public void SiloProviderRegistration_RejectsExistingUnkeyedProvider()
            {
                var services = new ServiceCollection();
                services.AddSingleton<ISiloConnectionTokenProvider>(new TestTokenProvider("existing"));
                var builder = new SiloConnectionAuthenticationBuilder(
                    "silo",
                    ConnectionAuthenticationServiceKeys.Silo,
                    new SiloConnectionAuthenticationOptions(),
                    services);

                Assert.Throws<InvalidOperationException>(
                    () => builder.UseTokenProvider(new TestTokenProvider("replacement")));
            }

            [Theory]
            [InlineData(false, true)]
            [InlineData(true, false)]
            [InlineData(true, true)]
            public void RequiredMode_RejectsDirectTlsAuthenticationCallbacks(
                bool configureClientCallback,
                bool configureServerCallback)
            {
                var tlsOptions = new TlsOptions();
                if (configureClientCallback)
                {
                    tlsOptions.OnAuthenticateAsClient = static (_, _) => { };
                }

                if (configureServerCallback)
                {
                    tlsOptions.OnAuthenticateAsServer = static (_, _) => { };
                }

                var exception = Assert.Throws<InvalidOperationException>(() =>
                    ConnectionAuthenticationRegistration.ConfigureApplicationProtocols(
                        tlsOptions,
                        new SiloConnectionAuthenticationOptions
                        {
                            Mode = SiloConnectionAuthenticationMode.Required,
                        }));

                Assert.Contains("does not permit", exception.Message, StringComparison.Ordinal);
            }

            [Theory]
            [InlineData(SiloConnectionAuthenticationMode.Audit)]
            [InlineData(SiloConnectionAuthenticationMode.Required)]
            public void EnabledModes_RequireOnlyServicesUsedByConnectionDirection(
                SiloConnectionAuthenticationMode mode)
            {
                var clientOptions = new SiloConnectionAuthenticationOptions
                {
                    Mode = mode,
                    TargetHost = "gateway.test",
                };
                var clientRegistration = new ClientConnectionAuthenticationRegistration(
                    "client",
                    new object(),
                    clientOptions,
                    new TlsOptions(),
                    hasTokenProvider: true,
                    hasTokenValidator: false);
                var gatewayOptions = new SiloConnectionAuthenticationOptions { Mode = mode };
                var gatewayRegistration = new GatewayConnectionAuthenticationRegistration(
                    "gateway",
                    new object(),
                    gatewayOptions,
                    new TlsOptions(),
                    hasTokenProvider: false,
                    hasTokenValidator: true);

                Assert.True(new SiloConnectionAuthenticationOptionsValidator(clientRegistration)
                    .Validate("client", clientOptions).Succeeded);
                Assert.True(new SiloConnectionAuthenticationOptionsValidator(gatewayRegistration)
                    .Validate("gateway", gatewayOptions).Succeeded);
            }

            [Theory]
            [InlineData(SiloConnectionAuthenticationMode.Audit)]
            [InlineData(SiloConnectionAuthenticationMode.Required)]
            public void EnabledModes_RejectMissingDirectionalServices(
                SiloConnectionAuthenticationMode mode)
            {
                var clientOptions = new SiloConnectionAuthenticationOptions
                {
                    Mode = mode,
                    TargetHost = "gateway.test",
                };
                var clientRegistration = new ClientConnectionAuthenticationRegistration(
                    "client",
                    new object(),
                    clientOptions,
                    new TlsOptions(),
                    hasTokenProvider: false,
                    hasTokenValidator: false);
                var gatewayOptions = new SiloConnectionAuthenticationOptions { Mode = mode };
                var gatewayRegistration = new GatewayConnectionAuthenticationRegistration(
                    "gateway",
                    new object(),
                    gatewayOptions,
                    new TlsOptions(),
                    hasTokenProvider: false,
                    hasTokenValidator: false);

                var clientResult = new SiloConnectionAuthenticationOptionsValidator(clientRegistration)
                    .Validate("client", clientOptions);
                var gatewayResult = new SiloConnectionAuthenticationOptionsValidator(gatewayRegistration)
                    .Validate("gateway", gatewayOptions);

                Assert.False(clientResult.Succeeded);
                Assert.Contains($"{mode} mode needs exactly one token provider.", clientResult.FailureMessage);
                Assert.False(gatewayResult.Succeeded);
                Assert.Contains($"{mode} mode needs exactly one token validator.", gatewayResult.FailureMessage);
            }

            private sealed class TestTokenProvider(string value) : ISiloConnectionTokenProvider
            {
                public ValueTask<SiloConnectionToken> GetTokenAsync(
                    SiloConnectionTokenRequestContext context,
                    CancellationToken cancellationToken) =>
                    ValueTask.FromResult(new SiloConnectionToken(value, DateTimeOffset.UtcNow.AddMinutes(5)));
            }
        }
    }
}
