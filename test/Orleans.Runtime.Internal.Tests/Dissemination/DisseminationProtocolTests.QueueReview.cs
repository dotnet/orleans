#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.Scheduler;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Fact]
    public void DisseminationKeysSortAcrossSupportedKinds()
    {
        var firstSilo = CreateSilo(40101);
        var secondSilo = CreateSilo(40102);
        DisseminationKey[] keys = [secondSilo, "b", DisseminationKey.Default, firstSilo, string.Empty, "a"];
        DisseminationKey[] expected = [DisseminationKey.Default, string.Empty, "a", "b", firstSilo, secondSilo];

        Array.Sort(keys);

        Assert.Equal(expected, keys);
        for (var left = 0; left < expected.Length; left++)
        {
            for (var right = 0; right < expected.Length; right++)
            {
                Assert.Equal(Math.Sign(left - right), Math.Sign(expected[left].CompareTo(expected[right])));
                Assert.Equal(Math.Sign(left - right), Math.Sign(((IComparable)expected[left]).CompareTo(expected[right])));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDrainRetriesUntilAcceptedWorkIsAcknowledged(bool transportFailure)
    {
        var local = CreateSilo(40111);
        var peer = CreateSilo(40112);
        var transport = new FakeTransport(local, peer);
        var clock = new FakeTimeProvider();
        var ns = new FakeNamespace(local);
        ns.SetValue(FakeNamespace.DefaultKey, 2);
        var sentVersions = new List<long>();
        transport.SendBroadcastResponseHandler = (_, batch, _) =>
        {
            sentVersions.Add(Assert.Single(GetBroadcastValues(batch)).Value.ToVersion);
            return sentVersions.Count == 1
                ? transportFailure
                    ? Task.FromException<DisseminationBroadcastResponse>(new InvalidOperationException("Transient send failure."))
                    : Task.FromResult(new DisseminationBroadcastResponse())
                : Task.FromResult(FakeTransport.CreateAcknowledgment(batch));
        };
        var queue = CreateBroadcastQueue(transport, [ns], timeProvider: clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var schedules = new BroadcastScheduleObserver();
        var retry = schedules.WaitAsync(
            value => value.LocalSilo.Equals(local) && value.Peer.Equals(peer)
                && value.Reason == DisseminationBroadcastScheduleReason.Retry,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(queue.Notify(peer, ns, FakeNamespace.DefaultKey));
        var stop = queue.StopAsync(cancellation.Token);
        try
        {
            var scheduled = await retry;
            Assert.False(stop.IsCompleted);
            Assert.False(queue.Notify(peer, ns, "after-stop"));
            Assert.Equal(new[] { 2L }, sentVersions);

            clock.Advance(scheduled.DueTime);
            await stop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(new[] { 2L, 2L }, sentVersions);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await stop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    [Fact]
    public async Task ShutdownDrainCancellationSurfacesIncompleteDelivery()
    {
        var local = CreateSilo(40121);
        var peer = CreateSilo(40122);
        var transport = new FakeTransport(local, peer);
        var clock = new FakeTimeProvider();
        var ns = new FakeNamespace(local);
        ns.SetValue(FakeNamespace.DefaultKey, 1);
        var sends = 0;
        transport.SendBroadcastResponseHandler = (_, _, _) =>
        {
            Interlocked.Increment(ref sends);
            return Task.FromResult(new DisseminationBroadcastResponse());
        };
        var queue = CreateBroadcastQueue(transport, [ns], timeProvider: clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var schedules = new BroadcastScheduleObserver();
        var retry = schedules.WaitAsync(
            value => value.LocalSilo.Equals(local) && value.Reason == DisseminationBroadcastScheduleReason.Retry,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(queue.Notify(peer, ns, FakeNamespace.DefaultKey));
        var stop = queue.StopAsync(cancellation.Token);
        try
        {
            await retry;
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
        }

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref sends));
    }

    [Fact]
    public async Task UnsupportedResponsePreservesPublicationsMadeDuringItsSend()
    {
        var local = CreateSilo(40131);
        var peer = CreateSilo(40132);
        var transport = new FakeTransport(local, peer);
        var clock = new FakeTimeProvider();
        var ns = new FakeNamespace(local);
        ns.SetValue("updated", 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new List<DisseminationBroadcastBatch>();
        transport.SendBroadcastResponseHandler = async (_, batch, cancellationToken) =>
        {
            batches.Add(batch);
            if (batches.Count == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return new DisseminationBroadcastResponse { UnsupportedNamespaces = [ns.Name] };
            }

            return FakeTransport.CreateAcknowledgment(batch);
        };
        var queue = CreateBroadcastQueue(transport, [ns], timeProvider: clock);
        try
        {
            Assert.True(queue.Notify(peer, ns, "updated"));
            var firstFlush = queue.FlushPendingBroadcast(TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            ns.SetValue("updated", 2);
            ns.SetValue("new", 7);
            Assert.True(queue.Notify(peer, ns, "updated"));
            Assert.True(queue.Notify(peer, ns, "new"));
            var newerFlush = queue.FlushPendingBroadcast(TestContext.Current.CancellationToken);
            release.SetResult();
            await Task.WhenAll(firstFlush, newerFlush).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(2, batches.Count);
            Assert.Equal(1, Assert.Single(GetBroadcastValues(batches[0])).Value.ToVersion);
            var delivered = GetBroadcastValues(batches[1]).ToDictionary(value => value.Value.Key, value => value.Value.ToVersion);
            Assert.Equal(2, delivered.Count);
            Assert.Equal(2, delivered["updated"]);
            Assert.Equal(7, delivered["new"]);
            await queue.FlushPendingBroadcast(TestContext.Current.CancellationToken);
            Assert.Equal(2, batches.Count);
        }
        finally
        {
            release.TrySetResult();
            await queue.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DisabledAntiEntropySleepsUntilOptionsChangeAndUnsubscribesOnStop()
    {
        var local = CreateSilo(40141);
        var peer = CreateSilo(40142);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var clock = new ReviewTimeProvider();
        var options = new ReviewOptionsMonitor(new DisseminationOptions());
        var exchanges = Channel.CreateUnbounded<int>();
        var exchangeCount = 0;
        transport.ExchangeAntiEntropyHandler = (destination, _, _) =>
        {
            exchanges.Writer.TryWrite(Interlocked.Increment(ref exchangeCount));
            return ValueTask.FromResult(new DisseminationAntiEntropyResponse { Sender = destination });
        };
        var localDetails = new FakeLocalSiloDetails(local);
        var target = new DisseminationSystemTarget(
            localDetails,
            transport.GrainFactory,
            new DisseminationMembership(transport.MembershipManager, localDetails, Options.Create(options.CurrentValue)),
            options,
            [ns],
            clock,
            NullLogger<DisseminationProtocol>.Instance,
            NullLogger<DisseminationBroadcastQueue>.Instance,
            CreatePhase4SystemTargetShared(local));
        var lifecycle = Substitute.For<ISiloLifecycle>();
        ILifecycleObserver? observer = null;
        lifecycle.Subscribe(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ILifecycleObserver>()).Returns(call =>
        {
            observer = call.ArgAt<ILifecycleObserver>(2);
            return new ReviewSubscription(static () => { });
        });
        ((ILifecycleParticipant<ISiloLifecycle>)target).Participate(lifecycle);
        Assert.NotNull(observer);
        await observer.OnStart(TestContext.Current.CancellationToken);
        try
        {
            await clock.WaitForChange(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
            await clock.WaitForChange(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromDays(1));
            await target.RunOrQueueTask(_ => Task.FromResult(true), TestContext.Current.CancellationToken);
            Assert.Equal(0, Volatile.Read(ref exchangeCount));

            options.Set(new DisseminationOptions { Enabled = true });
            Assert.Equal(1, await exchanges.Reader.ReadAsync(TestContext.Current.CancellationToken));
            await clock.WaitForChange(options.CurrentValue.Overlay.AntiEntropyInterval, TestContext.Current.CancellationToken);

            options.Set(new DisseminationOptions());
            await clock.WaitForChange(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromDays(1));
            await target.RunOrQueueTask(_ => Task.FromResult(true), TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref exchangeCount));

            options.Set(new DisseminationOptions { Enabled = true });
            Assert.Equal(2, await exchanges.Reader.ReadAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            shutdown.CancelAfter(TimeSpan.FromSeconds(5));
            await observer.OnStop(shutdown.Token);
        }

        Assert.Equal(0, options.SubscriptionCount);
    }

    private sealed class ReviewOptionsMonitor(DisseminationOptions initial) : IOptionsMonitor<DisseminationOptions>
    {
        private readonly object _lock = new();
        private readonly List<Action<DisseminationOptions, string?>> _listeners = [];
        private DisseminationOptions _current = initial;

        public DisseminationOptions CurrentValue => Volatile.Read(ref _current);
        public DisseminationOptions Get(string? name) => CurrentValue;
        public int SubscriptionCount
        {
            get
            {
                lock (_lock)
                {
                    return _listeners.Count;
                }
            }
        }

        public IDisposable OnChange(Action<DisseminationOptions, string?> listener)
        {
            lock (_lock)
            {
                _listeners.Add(listener);
            }

            return new ReviewSubscription(() =>
            {
                lock (_lock)
                {
                    _listeners.Remove(listener);
                }
            });
        }

        public void Set(DisseminationOptions value)
        {
            Volatile.Write(ref _current, value);
            Action<DisseminationOptions, string?>[] listeners;
            lock (_lock)
            {
                listeners = [.. _listeners];
            }

            foreach (var listener in listeners)
            {
                listener(value, null);
            }
        }
    }

    private sealed class ReviewSubscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }

    private sealed class ReviewTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        private readonly Channel<TimeSpan> _changes = Channel.CreateUnbounded<TimeSpan>();

        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public void Advance(TimeSpan duration) => _inner.Advance(duration);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ReviewTimer(this, _inner.CreateTimer(callback, state, dueTime, period));
            _changes.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task WaitForChange(TimeSpan expected, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (await _changes.Reader.ReadAsync(deadline.Token) != expected)
            {
            }
        }

        private sealed class ReviewTimer(ReviewTimeProvider owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var changed = inner.Change(dueTime, period);
                owner._changes.Writer.TryWrite(dueTime);
                return changed;
            }

            public void Dispose() => inner.Dispose();
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
