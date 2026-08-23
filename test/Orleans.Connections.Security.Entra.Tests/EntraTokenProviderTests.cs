using Azure.Core;
using Orleans.Connections.Security.Entra;

namespace Orleans.Connections.Security.Entra.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Security")]
public sealed class EntraTokenProviderTests
{
    [Fact]
    public async Task RequestsEachTokenFromCallerSuppliedCredential()
    {
        var options = EntraTestFixture.CreateOptions();
        var timeProvider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var credential = new TestTokenCredential(
            (_, _) => ValueTask.FromResult(new AccessToken("token", timeProvider.GetUtcNow().AddMinutes(10))));
        var provider = new EntraTokenProvider(credential, options, timeProvider);

        await provider.GetTokenAsync(CancellationToken.None);
        await provider.GetTokenAsync(CancellationToken.None);

        Assert.Equal(2, credential.CallCount);
    }

    [Fact]
    public async Task RejectsTokenWithInsufficientRemainingLifetimeWithoutLeakingIt()
    {
        const string token = "secret-bearer-token";
        var options = EntraTestFixture.CreateOptions();
        var timeProvider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var credential = new TestTokenCredential(
            (_, _) => ValueTask.FromResult(new AccessToken(token, timeProvider.GetUtcNow().AddSeconds(30))));
        var provider = new EntraTokenProvider(credential, options, timeProvider);

        var exception = await Assert.ThrowsAsync<EntraAuthenticationException>(
            () => provider.GetTokenAsync(CancellationToken.None).AsTask());

        Assert.Equal(EntraAuthenticationError.TokenAcquisitionFailed, exception.Error);
        Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestsConfiguredClusterScope()
    {
        var options = EntraTestFixture.CreateOptions();
        options.TokenScope = "api://11111111-1111-1111-1111-111111111111/cluster-a";
        options.ResourceApplicationId = "44444444-4444-4444-4444-444444444444";
        options.ClusterRole = "Orleans.Silo.Connect.cluster-a";
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        var credential = new TestTokenCredential(
            (_, _) => ValueTask.FromResult(new AccessToken("acquired-token", timeProvider.GetUtcNow().AddMinutes(10))));
        var provider = new EntraTokenProvider(credential, options, timeProvider);

        var token = await provider.GetTokenAsync(CancellationToken.None);

        var requestContext = credential.LastRequestContext;
        Assert.True(requestContext.HasValue);
        var scopes = requestContext.Value.Scopes;
        Assert.Equal(["api://11111111-1111-1111-1111-111111111111/cluster-a/.default"], scopes);
        Assert.DoesNotContain(options.ResourceApplicationId, scopes);
        Assert.DoesNotContain(options.ClusterRole, scopes);
        Assert.Equal(1, credential.CallCount);
        Assert.Equal("acquired-token", token.Token);
    }

    [Fact]
    public async Task RequestsConfiguredClusterScope_WhenAlreadySuffixed_DoesNotDuplicateDefaultSuffix()
    {
        var options = EntraTestFixture.CreateOptions();
        options.TokenScope = "api://11111111-1111-1111-1111-111111111111/cluster-a/.default";
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        var credential = new TestTokenCredential(
            (_, _) => ValueTask.FromResult(new AccessToken("already-suffixed-token", timeProvider.GetUtcNow().AddMinutes(10))));
        var provider = new EntraTokenProvider(credential, options, timeProvider);

        var token = await provider.GetTokenAsync(CancellationToken.None);

        var requestContext = credential.LastRequestContext;
        Assert.True(requestContext.HasValue);
        Assert.Equal(
            ["api://11111111-1111-1111-1111-111111111111/cluster-a/.default"],
            requestContext.Value.Scopes);
        Assert.Equal(1, credential.CallCount);
        Assert.Equal("already-suffixed-token", token.Token);
    }

    [Fact]
    public async Task RequestsConfiguredClusterScope_WhenDefaultSuffixHasTrailingSlash_NormalizesBeforeCheckingSuffix()
    {
        var options = EntraTestFixture.CreateOptions();
        options.TokenScope = "api://11111111-1111-1111-1111-111111111111/cluster-a/.default/";
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        var credential = new TestTokenCredential(
            (_, _) => ValueTask.FromResult(new AccessToken("normalized-suffix-token", timeProvider.GetUtcNow().AddMinutes(10))));
        var provider = new EntraTokenProvider(credential, options, timeProvider);

        var token = await provider.GetTokenAsync(CancellationToken.None);

        var requestContext = credential.LastRequestContext;
        Assert.True(requestContext.HasValue);
        Assert.Equal(
            ["api://11111111-1111-1111-1111-111111111111/cluster-a/.default"],
            requestContext.Value.Scopes);
        Assert.Equal(1, credential.CallCount);
        Assert.Equal("normalized-suffix-token", token.Token);
    }

    [Fact]
    public async Task RequestsConfiguredClusterScope_WhenTrailingSlash_NormalizesBeforeDefaultSuffix()
    {
        var options = EntraTestFixture.CreateOptions();
        options.TokenScope = "api://11111111-1111-1111-1111-111111111111/cluster-a/";
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        var credential = new TestTokenCredential(
            (_, _) => ValueTask.FromResult(new AccessToken("trailing-slash-token", timeProvider.GetUtcNow().AddMinutes(10))));
        var provider = new EntraTokenProvider(credential, options, timeProvider);

        var token = await provider.GetTokenAsync(CancellationToken.None);

        var requestContext = credential.LastRequestContext;
        Assert.True(requestContext.HasValue);
        Assert.Equal(
            ["api://11111111-1111-1111-1111-111111111111/cluster-a/.default"],
            requestContext.Value.Scopes);
        Assert.Equal(1, credential.CallCount);
        Assert.Equal("trailing-slash-token", token.Token);
    }
}
