using Xunit;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class MembershipTableModelBasedTestRunnerTests
{
    [Theory]
    [InlineData(2, 3)]
    [InlineData(9, 3)]
    [InlineData(3, 2)]
    [InlineData(3, 33)]
    public void Options_InvalidBounds_RejectBeforeAcquiringFixture(int depth, int length)
    {
        var calls = 0;
        var options = new MembershipTableModelBasedConformanceOptions { MaxDepth = depth, MaxSequenceLength = length };
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new MembershipTableModelBasedTestRunner(() =>
        {
            calls++;
            return new IdealizedMembershipBackend().Fixture();
        }, options));
        Assert.Equal("options", exception.ParamName);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Constructor_NullFactoryOrOptions_ReportsExactParameter()
    {
        Assert.Equal("fixtureFactory", Assert.Throws<ArgumentNullException>(() => new MembershipTableModelBasedTestRunner(null!, "provider")).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() =>
            new MembershipTableModelBasedTestRunner(() => new IdealizedMembershipBackend().Fixture(), (MembershipTableModelBasedConformanceOptions)null!)).ParamName);
        Assert.Throws<ArgumentException>(() => new MembershipTableModelBasedTestRunner(() => new IdealizedMembershipBackend().Fixture(), ""));
    }

    [Fact]
    public async Task Run_PreCancelledToken_DoesNotAcquireAnyFixture()
    {
        var calls = 0;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var runner = new MembershipTableModelBasedTestRunner(() =>
        {
            calls++;
            return new IdealizedMembershipBackend().Fixture();
        }, "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunGeneratedConformanceTests(cancelled.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RunGeneratedCases_HaveFreshScopesAndDisposeEveryAcquiredHandle()
    {
        var backend = new IdealizedMembershipBackend { LagHeartbeatReads = true };
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<string>();
        var runner = new MembershipTableModelBasedTestRunner(() =>
        {
            var fixture = backend.Fixture();
            Assert.True(scopes.Add(fixture.ClusterId));
            Assert.True(scopes.Add(fixture.OtherClusterId));
            return fixture;
        }, new MembershipTableModelBasedConformanceOptions { ProviderName = "owned-model", Seed = 31 }, messages.Add);
        await runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
        Assert.True(scopes.Count > 2);
        Assert.Equal(scopes.Count / 2 * 3, backend.CreatedHandles);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
        Assert.Empty(backend.Partitions);
        var summary = Assert.Single(messages, m => m.Contains("Accordant cases=", StringComparison.Ordinal));
        Assert.Contains("seed=31", summary);
        Assert.Contains("StartSuccessor", summary);
        Assert.Contains("UpdateStaleTable", summary);
        Assert.Contains("UpdateStaleSnapshot", summary);
        Assert.Contains("UpdateAfterHeartbeat", summary);
    }
}
