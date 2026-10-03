#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Fact]
    public async Task ShutdownDrainsAdmittedPublicationBeforeBroadcastQueue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40501);
        var peer = CreateSilo(40502);
        var transport = new FakeTransport(local, peer);
        var ns = new ProtocolReviewNamespace(local);
        ns.Inner.SetValue("value", 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var repairs = 0;
        ns.RepairHandler = request =>
        {
            if (Interlocked.Increment(ref repairs) == 1)
            {
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            }

            return ns.Inner.CreateRepair(request);
        };
        var protocol = CreateProtocol(transport, [ns], timeProvider: new FakeTimeProvider());
        var publication = Task.Run(async () => await protocol.Publish(ns, "value", 1, cancellationToken), cancellationToken);
        Task stop = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            stop = protocol.StopAsync(cancellationToken);
            Assert.False(stop.IsCompleted);
            Assert.False(await protocol.Publish(ns, "value", 1, cancellationToken));
            Assert.Equal(1, Volatile.Read(ref repairs));
            release.Set();

            Assert.True(await publication.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var batch = Assert.Single(transport.BroadcastBatches);
            Assert.Equal(peer, batch.Peer);
            Assert.Equal(1, Assert.Single(GetBroadcastValues(batch.Batch)).Value.ToVersion);
        }
        finally
        {
            release.Set();
            await publication.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task ShutdownDrainsAdmittedBroadcastApplicationAndForwarding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40511);
        var sender = CreateSilo(40512);
        var child = CreateSilo(40513);
        var transport = new FakeTransport(local, sender, child);
        var ns = new ProtocolReviewNamespace(local);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ns.ApplyHandler = async (value, token) =>
        {
            await release.Task.WaitAsync(token);
            return await ns.Inner.ApplyValueAsync(value, token);
        };
        var protocol = CreateProtocol(transport, [ns], options => options.Overlay.FanOutFactor = static _ => 2);
        var batch = new DisseminationBroadcastBatch
        {
            Sender = sender,
            Values = CreateValueGroups(ns.Name, ns.Inner.CreateItem(sender, "value", 1)),
        };
        var receive = protocol.ReceiveBroadcast(batch, cancellationToken);
        Task stop = Task.CompletedTask;
        try
        {
            Assert.Single(ns.Attempts);
            stop = protocol.StopAsync(cancellationToken);
            Assert.False(stop.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => protocol.ReceiveBroadcast(batch, cancellationToken));
            Assert.Single(ns.Attempts);
            release.SetResult();

            var response = await receive.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(1, Assert.Single(response.Acknowledgments[ns.Name]).Version);
            Assert.Equal(1, ns.GetVersion("value"));
            Assert.Equal(child, Assert.Single(transport.BroadcastBatches).Peer);
        }
        finally
        {
            release.TrySetResult();
            await receive.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task ShutdownDrainsAdmittedAntiEntropyResponse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40521);
        var peer = CreateSilo(40522);
        var transport = new FakeTransport(local, peer);
        var ns = new ProtocolReviewNamespace(local);
        ns.Inner.SetValue("value", 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        ns.RepairHandler = request =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            return ns.Inner.CreateRepair(request);
        };
        var protocol = CreateProtocol(transport, [ns]);
        var request = new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            SupportedNamespaces = [ns.Name],
            Digests = CreateAntiEntropyRequestDigest(ns.Name, ("value", 0)),
        };
        var response = Task.Run(async () => await protocol.ReceiveAntiEntropy(request, cancellationToken), cancellationToken);
        Task stop = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            stop = protocol.StopAsync(cancellationToken);
            Assert.False(stop.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await protocol.ReceiveAntiEntropy(request, cancellationToken));
            release.Set();

            var result = await response.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(1, Assert.Single(result.Values[ns.Name]).Value.ToVersion);
        }
        finally
        {
            release.Set();
            await response.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task ShutdownCancelsAndDrainsLocalAntiEntropyRound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40531);
        var peer = CreateSilo(40532);
        var transport = new FakeTransport(local, peer);
        var raw = new TaskCompletionSource<DisseminationAntiEntropyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.ExchangeAntiEntropyHandler = (_, _, token) =>
        {
            started.SetResult(token);
            return new(raw.Task);
        };
        var protocol = CreateProtocol(transport, new FakeNamespace(local));
        var round = protocol.RunAntiEntropyRound(cancellationToken);
        try
        {
            var transportToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await protocol.StopAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.True(transportToken.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => round.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.False(raw.Task.IsCompleted);
            await protocol.RunAntiEntropyRound(cancellationToken);
            Assert.Single(transport.AntiEntropyRequests);
        }
        finally
        {
            raw.TrySetResult(new DisseminationAntiEntropyResponse { Sender = peer });
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task ShutdownCancellationKeepsAdmissionClosed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40541);
        var peer = CreateSilo(40542);
        var transport = new FakeTransport(local, peer);
        var ns = new ProtocolReviewNamespace(local);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ns.ApplyHandler = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DisseminationApplyResult.Applied;
        };
        var protocol = CreateProtocol(transport, [ns], timeProvider: new FakeTimeProvider());
        var batch = new DisseminationBroadcastBatch
        {
            Sender = peer,
            Values = CreateValueGroups(ns.Name, ns.Inner.CreateItem(peer, "value", 1)),
        };
        var receive = protocol.ReceiveBroadcast(batch, requestCancellation.Token);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stop = protocol.StopAsync(shutdown.Token);
        try
        {
            Assert.Single(ns.Attempts);
            Assert.False(stop.IsCompleted);
            shutdown.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => stop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.Equal(shutdown.Token, exception.CancellationToken);
            Assert.False(receive.IsCompleted);
            Assert.False(await protocol.Publish(ns, "value", 1, cancellationToken));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => protocol.ReceiveBroadcast(batch, cancellationToken));
        }
        finally
        {
            requestCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => receive.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            await protocol.StopAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        Assert.Equal(0, ns.GetVersion("value"));
        Assert.Empty(transport.BroadcastBatches);
    }

    [Fact]
    public async Task ShutdownRejectsPublicationWithNoPeersAndPreservesCallerCancellation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40551);
        var transport = new FakeTransport(local);
        var ns = new FakeNamespace(local);
        ns.SetValue("value", 1);
        var protocol = CreateProtocol(transport, ns);
        await protocol.StopAsync(cancellationToken);

        Assert.False(await protocol.Publish(ns, "value", 1, cancellationToken));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await protocol.Publish(ns, "value", 1, canceled.Token));
        Assert.Equal(canceled.Token, exception.CancellationToken);
        Assert.Empty(transport.BroadcastBatches);
    }
}
