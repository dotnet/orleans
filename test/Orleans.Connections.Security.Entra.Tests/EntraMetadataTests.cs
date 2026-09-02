using System.Net;
using System.Net.Http;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using Orleans.Connections.Security.Entra;

namespace Orleans.Connections.Security.Entra.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Security")]
public sealed class EntraMetadataTests
{
    [Fact]
    public async Task RefreshesMetadataForSigningKeyRollover()
    {
        using var fixture = new EntraTestFixture();
        using var provider = CreateProvider(fixture);
        var validator = new EntraJwtValidator(fixture.Options, provider, fixture.TimeProvider);
        await validator.ValidateAsync(
            fixture.CreateToken(),
            EntraTestFixture.ClusterId,
            CancellationToken.None);
        var nextKey = fixture.CreateKey("key-2");
        fixture.RollMetadataTo(nextKey);

        var result = await validator.ValidateAsync(
            fixture.CreateToken(signingCredentials: nextKey),
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
        Assert.Equal(4, fixture.Metadata.RequestCount);
    }

    [Fact]
    public async Task RejectsOutOfScopeSigningKeyDuringRollover()
    {
        using var fixture = new EntraTestFixture();
        using var provider = CreateProvider(fixture);
        var validator = new EntraJwtValidator(fixture.Options, provider, fixture.TimeProvider);
        await validator.ValidateAsync(
            fixture.CreateToken(),
            EntraTestFixture.ClusterId,
            CancellationToken.None);
        var nextKey = fixture.CreateKey("key-2");
        fixture.Metadata.SetConfiguration(
            EntraTestFixture.Issuer,
            nextKey,
            keyCloudInstanceName: "microsoftonline.us",
            configurationCloudInstanceName: "microsoftonline.com");

        var exception = await Assert.ThrowsAsync<EntraAuthenticationException>(
            () => validator.ValidateAsync(
                fixture.CreateToken(signingCredentials: nextKey),
                EntraTestFixture.ClusterId,
                CancellationToken.None).AsTask());

        Assert.Equal(EntraAuthenticationError.InvalidToken, exception.Error);
    }

    [Fact]
    public async Task ThrottlesUnknownSigningKeyRefresh()
    {
        using var fixture = new EntraTestFixture();
        using var provider = CreateProvider(fixture);
        var validator = new EntraJwtValidator(fixture.Options, provider, fixture.TimeProvider);
        await validator.ValidateAsync(
            fixture.CreateToken(),
            EntraTestFixture.ClusterId,
            CancellationToken.None);
        var unknownKey = fixture.CreateKey("unknown");
        var token = fixture.CreateToken(signingCredentials: unknownKey);

        await Assert.ThrowsAsync<EntraAuthenticationException>(
            () => validator.ValidateAsync(token, EntraTestFixture.ClusterId, CancellationToken.None).AsTask());
        var afterFirstFailure = fixture.Metadata.RequestCount;
        await Assert.ThrowsAsync<EntraAuthenticationException>(
            () => validator.ValidateAsync(token, EntraTestFixture.ClusterId, CancellationToken.None).AsTask());

        Assert.Equal(4, afterFirstFailure);
        Assert.Equal(afterFirstFailure, fixture.Metadata.RequestCount);
    }

    [Fact]
    public async Task UsesLastKnownGoodMetadataDuringBoundedOutage()
    {
        using var fixture = new EntraTestFixture();
        fixture.Options.AutomaticMetadataRefreshInterval = TimeSpan.FromMinutes(1);
        fixture.Options.LastKnownGoodLifetime = TimeSpan.FromMinutes(10);
        using var provider = CreateProvider(fixture);
        var validator = new EntraJwtValidator(fixture.Options, provider, fixture.TimeProvider);
        await validator.ValidateAsync(
            fixture.CreateToken(),
            EntraTestFixture.ClusterId,
            CancellationToken.None);
        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        fixture.Metadata.FailRequests = true;

        var result = await validator.ValidateAsync(
            fixture.CreateToken(),
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
        Assert.Equal(3, fixture.Metadata.RequestCount);
    }

    [Fact]
    public async Task RejectsLastKnownGoodMetadataAfterBoundedLifetime()
    {
        using var fixture = new EntraTestFixture();
        fixture.Options.AutomaticMetadataRefreshInterval = TimeSpan.FromMinutes(1);
        fixture.Options.LastKnownGoodLifetime = TimeSpan.FromMinutes(5);
        using var provider = CreateProvider(fixture);
        var validator = new EntraJwtValidator(fixture.Options, provider, fixture.TimeProvider);
        await validator.ValidateAsync(
            fixture.CreateToken(),
            EntraTestFixture.ClusterId,
            CancellationToken.None);
        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        fixture.Metadata.FailRequests = true;

        var exception = await Assert.ThrowsAsync<EntraAuthenticationException>(
            () => validator.ValidateAsync(
                fixture.CreateToken(),
                EntraTestFixture.ClusterId,
                CancellationToken.None).AsTask());

        Assert.Equal(EntraAuthenticationError.ProviderUnavailable, exception.Error);
    }

    [Fact]
    public async Task AppliesBackoffAfterMetadataFailure()
    {
        using var fixture = new EntraTestFixture();
        fixture.Options.AutomaticMetadataRefreshInterval = TimeSpan.FromMinutes(1);
        fixture.Options.MetadataRefreshBackoff = TimeSpan.FromSeconds(10);
        fixture.Options.MaximumMetadataRefreshBackoff = TimeSpan.FromSeconds(10);
        using var provider = CreateProvider(fixture);
        var validator = new EntraJwtValidator(fixture.Options, provider, fixture.TimeProvider);
        await validator.ValidateAsync(
            fixture.CreateToken(),
            EntraTestFixture.ClusterId,
            CancellationToken.None);
        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        fixture.Metadata.FailRequests = true;
        await validator.ValidateAsync(
            fixture.CreateToken(),
            EntraTestFixture.ClusterId,
            CancellationToken.None);
        var requestCount = fixture.Metadata.RequestCount;

        await validator.ValidateAsync(
            fixture.CreateToken(),
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        Assert.Equal(requestCount, fixture.Metadata.RequestCount);
    }

    [Fact]
    public async Task MetadataRefreshIsSingleFlight()
    {
        using var fixture = new EntraTestFixture();
        fixture.Metadata.ResponseDelay = TimeSpan.FromMilliseconds(50);
        using var provider = CreateProvider(fixture);
        var validator = new EntraJwtValidator(fixture.Options, provider, fixture.TimeProvider);
        var token = fixture.CreateToken();

        await Task.WhenAll(
            Enumerable.Range(0, 20).Select(
                _ => validator.ValidateAsync(token, EntraTestFixture.ClusterId, CancellationToken.None).AsTask()));

        Assert.Equal(2, fixture.Metadata.RequestCount);
    }

    [Fact]
    public async Task MetadataRefreshQueueIsBounded()
    {
        using var fixture = new EntraTestFixture();
        fixture.Options.MaximumMetadataRefreshQueueSize = 2;
        fixture.Metadata.ResponseDelay = TimeSpan.FromMilliseconds(100);
        using var provider = CreateProvider(fixture);

        var operations = Enumerable.Range(0, 8)
            .Select(_ => provider.GetConfigurationAsync(CancellationToken.None).AsTask())
            .ToArray();
        var results = await Task.WhenAll(
            operations.Select(async operation =>
            {
                try
                {
                    await operation;
                    return EntraAuthenticationError.InvalidToken;
                }
                catch (EntraAuthenticationException exception)
                {
                    return exception.Error;
                }
            }));

        Assert.Contains(EntraAuthenticationError.ProviderUnavailable, results);
        Assert.Equal(2, fixture.Metadata.RequestCount);
    }

    [Theory]
    [InlineData("enc", null)]
    [InlineData("sig", "sign")]
    public async Task RejectsSigningKeysWithoutVerificationUsage(string use, string? keyOperation)
    {
        using var fixture = new EntraTestFixture();
        fixture.Metadata.SetConfiguration(
            EntraTestFixture.Issuer,
            fixture.CurrentKey,
            use,
            keyOperation is null ? null : [keyOperation]);
        using var provider = CreateProvider(fixture);

        var exception = await Assert.ThrowsAsync<EntraAuthenticationException>(
            () => provider.GetConfigurationAsync(CancellationToken.None).AsTask());

        Assert.Equal(EntraAuthenticationError.ProviderUnavailable, exception.Error);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.us/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0", null)]
    [InlineData(EntraTestFixture.Issuer, "https://login.microsoftonline.us/keys")]
    public async Task RejectsCrossCloudIssuerOrSigningKeySubstitution(string issuer, string? jwksUri)
    {
        using var fixture = new EntraTestFixture();
        fixture.Metadata.SetConfiguration(issuer, fixture.CurrentKey, jwksUri: jwksUri);
        using var provider = CreateProvider(fixture);

        var exception = await Assert.ThrowsAsync<EntraAuthenticationException>(
            () => provider.GetConfigurationAsync(CancellationToken.None).AsTask());

        Assert.Equal(EntraAuthenticationError.ProviderUnavailable, exception.Error);
    }

    [Fact]
    public async Task SupportsExplicitSovereignCloudAuthority()
    {
        const string issuer = "https://login.microsoftonline.us/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0";
        using var fixture = new EntraTestFixture();
        var options = EntraTestFixture.CreateOptions(issuer);
        var metadata = new TestDocumentRetriever(options.Authority!);
        metadata.SetConfiguration(issuer, fixture.CurrentKey);
        using var provider = new EntraOpenIdConfigurationProvider(options, metadata, fixture.TimeProvider, static () => 0);
        var validator = new EntraJwtValidator(options, provider, fixture.TimeProvider);

        var result = await validator.ValidateAsync(
            fixture.CreateToken(issuer: issuer),
            EntraTestFixture.ClusterId,
            CancellationToken.None);

        Assert.True(result.Principal.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task RejectsRedirectedMetadata()
    {
        var options = EntraTestFixture.CreateOptions();
        var handler = new TestHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://evil.example/metadata") },
            });
        using var retriever = new StrictHttpDocumentRetriever(options, handler);

        var exception = await Assert.ThrowsAsync<EntraAuthenticationException>(
            () => retriever.GetDocumentAsync(
                $"{options.Authority!.AbsoluteUri.TrimEnd('/')}/.well-known/openid-configuration",
                CancellationToken.None));

        Assert.Equal(EntraAuthenticationError.ProviderUnavailable, exception.Error);
    }

    [Fact]
    public async Task RejectsUntrustedMetadataHostBeforeSendingRequest()
    {
        var options = EntraTestFixture.CreateOptions();
        var handler = new TestHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var retriever = new StrictHttpDocumentRetriever(options, handler);

        await Assert.ThrowsAsync<EntraAuthenticationException>(
            () => retriever.GetDocumentAsync("https://evil.example/metadata", CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    private static EntraOpenIdConfigurationProvider CreateProvider(EntraTestFixture fixture)
        => new(fixture.Options, fixture.Metadata, fixture.TimeProvider, static () => 0);
}
