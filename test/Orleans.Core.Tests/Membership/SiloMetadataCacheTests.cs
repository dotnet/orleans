using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class SiloMetadataCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReentrantAndConcurrentSynchronousDisposalReturnsWhileAsyncDisposalDrains(bool synchronousOwner)
    {
        var fixture = new Fixture();
        var token = await fixture.StartAsync();
        var handle = token.WaitHandle;
        var reentered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var registration = token.Register(() =>
        {
            fixture.Cache.Dispose();
            reentered.SetResult();
            releaseCallback.Task.WaitAsync(timeout.Token).GetAwaiter().GetResult();
        });

        var owner = Task.Factory.StartNew(() =>
        {
            if (synchronousOwner)
            {
                fixture.Cache.Dispose();
                return Task.CompletedTask;
            }

            return fixture.Cache.DisposeAsync().AsTask();
        }, timeout.Token, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

        try
        {
            await reentered.Task.WaitAsync(timeout.Token);
            await Task.Run(fixture.Cache.Dispose, timeout.Token).WaitAsync(timeout.Token);
            var first = fixture.Cache.DisposeAsync().AsTask();
            var second = fixture.Cache.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.False(owner.IsCompleted);
            Assert.False(first.IsCompleted);
            Assert.True(handle.WaitOne(0));

            releaseCallback.SetResult();
            await fixture.SubscriptionStopping.Task.WaitAsync(timeout.Token);
            Assert.False(first.IsCompleted);
            Assert.True(handle.WaitOne(0));
            fixture.ReleaseSubscription.SetResult();
            await Task.WhenAll(owner.Unwrap(), first, second).WaitAsync(timeout.Token);

            Assert.Same(first, fixture.Cache.DisposeAsync().AsTask());
            Assert.Throws<ObjectDisposedException>(() => handle.WaitOne(0));
        }
        finally
        {
            releaseCallback.TrySetResult();
            fixture.ReleaseSubscription.TrySetResult();
            if (reentered.Task.IsCompleted)
            {
                await fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                registration.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalDrainsSubscriptionBeforeReleasingCancellation(bool callbackThrows)
    {
        await using var fixture = new Fixture();
        var token = await fixture.StartAsync();
        var handle = token.WaitHandle;
        var expected = new InvalidOperationException("Cancellation callback failed.");
        var callbackThread = 0;
        using var registration = token.Register(() =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            if (callbackThrows)
            {
                throw expected;
            }
        });

        var callerThread = Environment.CurrentManagedThreadId;
        var first = fixture.Cache.DisposeAsync().AsTask();
        var second = fixture.Cache.DisposeAsync().AsTask();
        await fixture.SubscriptionStopping.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(callerThread, callbackThread);
        Assert.Same(first, second);
        Assert.True(token.IsCancellationRequested);
        Assert.True(handle.WaitOne(0));
        Assert.False(first.IsCompleted);
        fixture.ReleaseSubscription.SetResult();

        if (callbackThrows)
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => first);
            Assert.Same(expected, Assert.Single(failure.InnerExceptions));
            Assert.Same(first, fixture.Cache.DisposeAsync().AsTask());
            Assert.Same(failure, Assert.Throws<AggregateException>(fixture.Cache.Dispose));
        }
        else
        {
            await first;
            fixture.Cache.Dispose();
        }

        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
        Assert.Throws<ObjectDisposedException>(() => handle.WaitOne(0));
    }

    [Fact]
    public async Task DisposalWaitsForAdmittedMetadataRequest()
    {
        await using var fixture = new Fixture();
        var target = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11111), 1);
        var requestStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<SiloMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.GetSiloMetadata(target, Arg.Any<CancellationToken>()).Returns(call =>
        {
            requestStarted.SetResult(call.Arg<CancellationToken>());
            return response.Task;
        });
        var token = await fixture.StartAsync();
        var handle = token.WaitHandle;
        fixture.Updates.Writer.TryWrite(new MembershipTableSnapshot(
            new MembershipVersion(1),
            ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(target, new MembershipEntry
            {
                SiloAddress = target,
                Status = SiloStatus.Active,
                IAmAliveTime = DateTime.UtcNow,
            })));
        try
        {
            var requestToken = await requestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            var disposal = fixture.Cache.DisposeAsync().AsTask();

            Assert.Equal(token, requestToken);
            Assert.False(handle.SafeWaitHandle.IsClosed);
            Assert.False(disposal.IsCompleted);
            var metadata = new SiloMetadata();
            metadata.AddMetadata("region", "west");
            response.SetResult(metadata);
            await fixture.SubscriptionStopping.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(requestToken.IsCancellationRequested);
            Assert.False(disposal.IsCompleted);
            fixture.ReleaseSubscription.SetResult();
            await disposal;

            Assert.Equal("west", fixture.Cache.GetSiloMetadata(target).Metadata["region"]);
            Assert.Throws<ObjectDisposedException>(() => handle.WaitOne(0));
        }
        finally
        {
            response.TrySetResult(SiloMetadata.Empty);
        }
    }

    [Fact]
    public async Task CanceledLifecycleStopPreservesSourceUntilSubscriptionExits()
    {
        await using var fixture = new Fixture();
        var token = await fixture.StartAsync();
        var handle = token.WaitHandle;
        using var stopping = new CancellationTokenSource();

        var stop = fixture.Lifecycle.OnStop(stopping.Token);
        await fixture.SubscriptionStopping.Task.WaitAsync(TestContext.Current.CancellationToken);
        stopping.Cancel();
        await stop;
        var disposal = fixture.Cache.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        Assert.True(handle.WaitOne(0));
        fixture.ReleaseSubscription.SetResult();
        await disposal;

        Assert.Throws<ObjectDisposedException>(() => handle.WaitOne(0));
    }

    [Fact]
    public async Task DisposalBeforeStartupReleasesSource()
    {
        await using var fixture = new Fixture();
        var source = Assert.IsType<CancellationTokenSource>(
            typeof(SiloMetadataCache).GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Cache));
        var handle = source.Token.WaitHandle;

        await fixture.Cache.DisposeAsync();
        fixture.Cache.Dispose();

        Assert.False(fixture.Subscription.Task.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => handle.WaitOne(0));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ISiloMetadataClient Client { get; } = Substitute.For<ISiloMetadataClient>();
        public SiloMetadataCache Cache { get; }
        public SiloLifecycleSubject Lifecycle { get; } = new(NullLogger<SiloLifecycleSubject>.Instance);
        public Channel<MembershipTableSnapshot> Updates { get; } = Channel.CreateUnbounded<MembershipTableSnapshot>();
        public TaskCompletionSource<CancellationToken> Subscription { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SubscriptionStopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSubscription { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture()
        {
            var membership = Substitute.For<IMembershipManager>();
            membership.MembershipUpdates.Returns(ObserveUpdates(CancellationToken.None));
            Cache = new(Client, membership, Options.Create(new ClusterMembershipOptions()),
                Substitute.For<ILocalSiloDetails>(), NullLogger<SiloMetadataCache>.Instance);
            ((ILifecycleParticipant<ISiloLifecycle>)Cache).Participate(Lifecycle);
        }

        public async Task<CancellationToken> StartAsync()
        {
            await Lifecycle.OnStart(TestContext.Current.CancellationToken);
            return await Subscription.Task.WaitAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseSubscription.TrySetResult();
            await Cache.DisposeAsync().AsTask().ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        private async IAsyncEnumerable<MembershipTableSnapshot> ObserveUpdates(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Subscription.TrySetResult(cancellationToken);
            try
            {
                await foreach (var update in Updates.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return update;
                }
            }
            finally
            {
                SubscriptionStopping.TrySetResult();
                await ReleaseSubscription.Task;
            }
        }
    }
}
