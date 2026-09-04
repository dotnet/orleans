using System.Runtime.CompilerServices;
using Azure.Core;
using Orleans.Connections.Security.Entra;

namespace Orleans.Connections.Security.Entra.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Security")]
public sealed class EntraSiloConnectionTokenProviderTests
{
    [Fact]
    public async Task GetTokenAsync_ForwardsConnectionContextAndReturnsAcquiredToken()
    {
        var expiresAt = new DateTimeOffset(2026, 8, 23, 12, 30, 0, TimeSpan.Zero);
        var expectedToken = new AccessToken("acquired-token", expiresAt);
        SiloConnectionTokenRequestContext? capturedContext = null;
        var underlyingProvider = new TestEntraTokenProvider((context, _) =>
        {
            capturedContext = context;
            return ValueTask.FromResult(expectedToken);
        });
        var provider = new EntraSiloConnectionTokenProvider(underlyingProvider);
        var requestContext = (SiloConnectionTokenRequestContext)RuntimeHelpers.GetUninitializedObject(
            typeof(SiloConnectionTokenRequestContext));

        var result = await provider.GetTokenAsync(requestContext, CancellationToken.None);

        Assert.Same(requestContext, capturedContext);
        Assert.Equal(1, underlyingProvider.CallCount);
        Assert.Equal(expectedToken.Token, result.Value);
        Assert.Equal(expectedToken.ExpiresOn, result.ExpiresAt);
    }

    [Fact]
    public async Task GetTokenAsync_ForwardsCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var expectedToken = new AccessToken(
            "cancellation-token",
            new DateTimeOffset(2026, 8, 23, 12, 30, 0, TimeSpan.Zero));
        CancellationToken capturedCancellationToken = default;
        var underlyingProvider = new TestEntraTokenProvider((_, cancellationToken) =>
        {
            capturedCancellationToken = cancellationToken;
            return ValueTask.FromResult(expectedToken);
        });
        var provider = new EntraSiloConnectionTokenProvider(underlyingProvider);
        var requestContext = (SiloConnectionTokenRequestContext)RuntimeHelpers.GetUninitializedObject(
            typeof(SiloConnectionTokenRequestContext));

        var result = await provider.GetTokenAsync(requestContext, cancellation.Token);

        Assert.Equal(cancellation.Token, capturedCancellationToken);
        Assert.Equal(1, underlyingProvider.CallCount);
        Assert.Equal(expectedToken.Token, result.Value);
        Assert.Equal(expectedToken.ExpiresOn, result.ExpiresAt);
    }

    [Fact]
    public void Constructor_NullProvider_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new EntraSiloConnectionTokenProvider(null!));

        Assert.Equal("provider", exception.ParamName);
    }

    private sealed class TestEntraTokenProvider(
        Func<SiloConnectionTokenRequestContext, CancellationToken, ValueTask<AccessToken>> getToken)
        : IEntraTokenProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<AccessToken> GetTokenAsync(
            SiloConnectionTokenRequestContext context,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return getToken(context, cancellationToken);
        }
    }
}
