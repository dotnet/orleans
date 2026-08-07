using Azure.Core;
using Orleans.Connections.Security.Entra;

namespace Orleans.Connections.Security.Entra.Tests;

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
}
