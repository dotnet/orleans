using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace DefaultCluster.Tests.General;

/// <summary>
/// Tests for <see cref="GrainBaseExtensions.RunRequestAsync(IGrainBase, Func{CancellationToken, Task}, CancellationToken)"/>,
/// which runs a delegate as a request on the grain's activation.
/// </summary>
[TestCategory("BVT"), TestCategory("Scheduler")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class RunRequestTests : HostedTestClusterEnsureDefaultStarted
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public RunRequestTests(DefaultClusterFixture fixture) : base(fixture) { }

    [Fact]
    public async Task NonReentrantGrain_RunsRequestAfterCurrentCall()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());

        await grain.StartRequestDuringCall();
        await grain.AwaitPendingRequest();

        Assert.Equal(["call-start", "call-end", "request"], await grain.GetLog());
    }

    [Fact]
    public async Task ReentrantGrain_InterleavesRequestWithCurrentCall()
    {
        var grain = GrainFactory.GetGrain<IReentrantRunRequestGrain>(Guid.NewGuid());

        await grain.StartRequestDuringCall();
        await grain.AwaitPendingRequest();

        Assert.Equal(["call-start", "request", "call-end"], await grain.GetLog());
    }

    [Fact]
    public async Task NonReentrantGrain_RequestExcludesGrainCalls()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());
        await grain.StartBlockingRequest();
        await WaitForLogEntryAsync(grain, "request-start");

        var ping = grain.Ping();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(ping.IsCompleted);

        await grain.ReleaseBlockingRequest();
        await ping;
        await grain.AwaitPendingRequest();

        Assert.Equal(["request-start", "request-end", "ping"], await grain.GetLog());
    }

    [Fact]
    public async Task ReentrantGrain_GrainCallsInterleaveWithRequest()
    {
        var grain = GrainFactory.GetGrain<IReentrantRunRequestGrain>(Guid.NewGuid());
        await grain.StartBlockingRequest();
        await WaitForLogEntryAsync(grain, "request-start");

        await grain.Ping();
        await grain.ReleaseBlockingRequest();
        await grain.AwaitPendingRequest();

        Assert.Equal(["request-start", "ping", "request-end"], await grain.GetLog());
    }

    [Fact]
    public async Task RunRequest_ReturnsCallbackResult()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());

        await grain.StartValueRequest(21);

        Assert.Equal("42", await grain.AwaitPendingRequest());
    }

    [Fact]
    public async Task RunRequest_StartsNewCallChain()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());

        await grain.StartRequestContextRequest("caller");

        Assert.Null(await grain.AwaitPendingRequest());
    }

    [Fact]
    public async Task RunRequest_PropagatesCallbackException()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());

        await grain.StartFaultingRequest();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(grain.AwaitPendingRequest);
        Assert.Equal("Request failed.", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunRequest_CanceledBeforeStart_DoesNotRunCallback(bool cancelBeforeQueue)
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());

        await grain.StartCanceledRequest(cancelBeforeQueue);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(grain.AwaitPendingRequest);
        Assert.Empty(await grain.GetLog());
    }

    [Fact]
    public async Task RunRequest_PassesThroughGrainCallFilter()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());

        await grain.StartValueRequest(1);
        await grain.AwaitPendingRequest();

        Assert.Contains("IGrainRequestInvoker.RunRequestAsync", await grain.GetFilteredCalls());
    }

    [Fact]
    public async Task NamedRequest_DescribesItselfToGrainCallFilter()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());

        await grain.StartNamedRequest(42);

        Assert.Equal("order-42", await grain.AwaitPendingRequest());
        Assert.Contains($"{RunRequestGrainBase.NamedRequestInterfaceName}.ProcessOrder", await grain.GetFilteredCalls());
        Assert.Equal(["argument:42"], await grain.GetLog());
    }

    [Fact]
    public async Task NonReentrantGrain_InterleavingRequestRunsDuringCurrentCall()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());

        await grain.StartInterleavingRequestDuringCall();
        await grain.AwaitPendingRequest();

        Assert.Equal(["call-start", "request", "call-end"], await grain.GetLog());
    }

    [Fact]
    public async Task RunRequest_FilterThrows_FaultsRequest()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());
        await grain.SetRequestFilterMode(RequestFilterMode.Throw);

        await grain.StartValueRequest(1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.AwaitPendingRequest().WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal("Filter failed.", exception.Message);
    }

    [Fact]
    public async Task RunRequest_FilterShortCircuits_CompletesWithFilterResult()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());
        await grain.SetRequestFilterMode(RequestFilterMode.ShortCircuit);

        await grain.StartValueRequest(1);

        Assert.Equal("filtered", await grain.AwaitPendingRequest().WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunRequest_FilterThrowsAfterInvoke_CompletesWithCallbackResult()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());
        await grain.SetRequestFilterMode(RequestFilterMode.ThrowAfterInvoke);

        await grain.StartValueRequest(21);

        Assert.Equal("42", await grain.AwaitPendingRequest().WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunRequest_FromOutsideActivation_RunsOnActivation()
    {
        var key = Guid.NewGuid();
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(key);
        await grain.RetainInstance();

        Assert.Equal("ran", await RunRequestGrainBase.RunRequestOnRetainedInstance(key).WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(["outside"], await grain.GetLog());
    }

    [Fact]
    public async Task RunRequest_OnDeactivatedActivation_IsRejected()
    {
        var key = Guid.NewGuid();
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(key);
        await grain.DeactivateAndRetainInstance();

        // Requests queued while the activation is still deactivating run; once it is invalid, they are rejected.
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            try
            {
                await RunRequestGrainBase.RunRequestOnRetainedInstance(key).WaitAsync(timeout.Token);
            }
            catch (OrleansMessageRejectionException)
            {
                return;
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task WaitForLogEntryAsync(IRunRequestGrain grain, string entry)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!(await grain.GetLog()).Contains(entry))
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}

/// <summary>
/// Tests that requests queued by <see cref="GrainBaseExtensions.RunRequestAsync(IGrainBase, Func{CancellationToken, Task}, CancellationToken)"/>
/// honor <see cref="SiloMessagingOptions.MaxEnqueuedRequestsHardLimit"/>.
/// </summary>
[TestCategory("BVT"), TestCategory("Scheduler")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class RunRequestQueueLimitTests : TestClusterPerTest
{
    private const int HardLimit = 3;

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 1;
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }

    [Fact]
    public async Task RunRequest_BeyondHardLimit_FaultsWithLimitExceeded()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());

        // The queuing call counts toward the limit, so the activation admits requests until its count exceeds the limit.
        var outcomes = await grain.QueueRequests(HardLimit + 2);
        await grain.AwaitPendingRequest().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["queued", "queued", "queued", nameof(LimitExceededException), nameof(LimitExceededException)], outcomes);
        Assert.Equal(["request-0", "request-1", "request-2"], await grain.GetLog());
    }

    [Fact]
    public async Task RunRequest_ConcurrentProducers_StayWithinHardLimit()
    {
        var grain = GrainFactory.GetGrain<INonReentrantRunRequestGrain>(Guid.NewGuid());

        var admitted = await grain.QueueRequestsConcurrently(64);
        await grain.AwaitPendingRequest().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(HardLimit, admitted);
        Assert.Equal(HardLimit, (await grain.GetLog()).Count);
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
            hostBuilder.Configure<SiloMessagingOptions>(options => options.MaxEnqueuedRequestsHardLimit = HardLimit);
    }
}
