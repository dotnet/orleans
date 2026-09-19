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
    public async Task RunGeneratedCases_ReturnsWhileAsyncInitializationIsPending()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var backend = new IdealizedMembershipBackend();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisitions = 0;
        var runner = new MembershipTableModelBasedTestRunner(() => new("async-initialization", async (_, cluster, _) =>
        {
            if (Interlocked.Increment(ref acquisitions) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }

            return new MembershipTableTestHandle(backend.Create(cluster), () => backend.DisposeHandleAsync(cluster));
        }, backend.IsDeletedAsync), "async-initialization");
        Task execution = Task.CompletedTask;
        var caller = Task.Run(() =>
        {
            execution = runner.RunGeneratedConformanceTests(ct);
            returned.TrySetResult();
        }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(ct);
            await returned.Task.WaitAsync(ct);
            Assert.False(execution.IsCompleted);
            Assert.Equal(0, backend.CreatedHandles);
        }
        finally
        {
            release.TrySetResult();
            await caller;
            await execution;
        }

        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
        Assert.Empty(backend.Partitions);
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
        Assert.Equal(959 * 2, scopes.Count);
        Assert.Equal(scopes.Count / 2 * 3, backend.CreatedHandles);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
        Assert.Empty(backend.Partitions);
        var firstCase = messages.Where(m => m.StartsWith("seed=31; case=0;", StringComparison.Ordinal)).ToArray();
        Assert.StartsWith("seed=31; case=0; phase=initialize;", firstCase[0]);
        Assert.StartsWith("seed=31; case=0; phase=initialized;", firstCase[1]);
        Assert.StartsWith("seed=31; case=0; phase=dispose;", firstCase[^2]);
        Assert.StartsWith("seed=31; case=0; phase=disposed;", firstCase[^1]);
        var firstStep = firstCase.Where(m => m.Contains("; step=1;", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, firstStep.Length);
        Assert.EndsWith("; phase=observe-before", firstStep[0]);
        Assert.EndsWith("; phase=invoke", firstStep[1]);
        Assert.EndsWith("; phase=observe-after", firstStep[2]);
        Assert.EndsWith("; phase=completed", firstStep[3]);
        var summary = Assert.Single(messages, m => m.Contains("Accordant cases=", StringComparison.Ordinal));
        Assert.Contains("seed=31", summary);
        Assert.Contains("StartSuccessor", summary);
        Assert.Contains("UpdateStaleTable", summary);
        Assert.Contains("UpdateStaleSnapshot", summary);
        Assert.Contains("UpdateAfterHeartbeat", summary);
    }
}
