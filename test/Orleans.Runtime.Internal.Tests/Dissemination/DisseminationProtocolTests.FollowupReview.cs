#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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
    public async Task CompletedAsyncApplicationWinsAConcurrentLocalWaitTimeout()
    {
        var local = CreateSilo(40401);
        var sender = CreateSilo(40402);
        var child = CreateSilo(40403);
        var clock = new FakeTimeProvider();
        var ns = new ProtocolReviewNamespace(local, "async-terminal-result-review");
        ns.Options.StaleItemTtl = TimeSpan.FromSeconds(1);
        var value = ns.Inner.CreateValue("value", 1);
        var application = new TaskCompletionSource<DisseminationApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ns.ApplyHandler = (_, _) => new(application.Task);
        var transport = new FakeTransport(local, sender, child);
        var protocol = CreateProtocol(transport, [ns], options => options.Overlay.FanOutFactor = static _ => 2, clock);
        var context = new MembershipReviewContinuationContext();
        var previousContext = SynchronizationContext.Current;
        Task<DisseminationBroadcastResponse> receive;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            receive = protocol.ReceiveBroadcast(new DisseminationBroadcastBatch
            {
                Sender = sender,
                Values = CreateValueGroups(ns.Name, CreateDisseminationValue(sender, value)),
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        try
        {
            clock.Advance(ns.Options.StaleItemTtl);
            var timedOutWait = await context.TakeContinuation(TestContext.Current.CancellationToken);
            Assert.False(receive.IsCompleted);
            ns.Inner.PublishValue(value);
            application.SetResult(DisseminationApplyResult.Applied);
            timedOutWait.Callback(timedOutWait.State);
            for (var index = 0; index < 4 && !receive.IsCompleted; index++)
            {
                var continuation = await context.TakeContinuation(TestContext.Current.CancellationToken);
                continuation.Callback(continuation.State);
            }

            var response = await receive.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, Assert.Single(response.Acknowledgments[ns.Name]).Version);
            await protocol.FlushPendingBroadcast(TestContext.Current.CancellationToken);
            Assert.Equal(child, Assert.Single(transport.BroadcastBatches).Peer);
            Assert.Equal(1, ns.GetVersion("value"));
            await protocol.RunAntiEntropyRound(TestContext.Current.CancellationToken);
            Assert.NotEmpty(transport.AntiEntropyRequests);
            Assert.All(transport.AntiEntropyRequests, request => Assert.False(request.Request.Digests.ContainsKey(ns.Name)));
        }
        finally
        {
            application.TrySetResult(DisseminationApplyResult.Rejected);
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CompletedApplicationRemainsAppliedWhenItsLocalDeadlineRacesCompletion()
    {
        var local = CreateSilo(39651);
        var sender = CreateSilo(39652);
        var child = CreateSilo(39653);
        var clock = new FakeTimeProvider();
        var ns = new ProtocolReviewNamespace(local, "terminal-result-review");
        ns.Options.StaleItemTtl = TimeSpan.FromSeconds(1);
        CancellationToken applicationToken = default;
        ns.ApplyHandler = (value, token) =>
        {
            applicationToken = token;
            var result = ns.Inner.ApplyValueAsync(value, token);
            Assert.True(result.IsCompletedSuccessfully);
            clock.Advance(ns.Options.StaleItemTtl);
            return result;
        };
        var transport = new FakeTransport(local, sender, child);
        var protocol = CreateProtocol(transport, [ns], options => options.Overlay.FanOutFactor = static _ => 2, clock);
        try
        {
            var response = await protocol.ReceiveBroadcast(new DisseminationBroadcastBatch
            {
                Sender = sender,
                Values = CreateValueGroups(ns.Name, ns.Inner.CreateItem(sender, "value", 1)),
            }, TestContext.Current.CancellationToken);

            Assert.True(applicationToken.IsCancellationRequested);
            Assert.Equal(1, ns.GetVersion("value"));
            Assert.Equal(1, Assert.Single(response.Acknowledgments[ns.Name]).Version);
            await protocol.FlushPendingBroadcast(TestContext.Current.CancellationToken);
            var forwarded = Assert.Single(transport.BroadcastBatches);
            Assert.Equal(child, forwarded.Peer);
            Assert.Equal(1, Assert.Single(GetBroadcastValues(forwarded.Batch)).Value.ToVersion);

            await protocol.RunAntiEntropyRound(TestContext.Current.CancellationToken);
            Assert.NotEmpty(transport.AntiEntropyRequests);
            Assert.All(transport.AntiEntropyRequests, request => Assert.False(request.Request.Digests.ContainsKey(ns.Name)));
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ChangedOversizedBatchCannotAcknowledgeOrApplyAMissingChainPrefix()
    {
        var local = CreateSilo(39661);
        var peer = CreateSilo(39662);
        var first = new FakeNamespace(local, "cursor-first");
        var second = new FakeNamespace(local, "cursor-second");
        second.ExpectedKeys.Add("stream");
        var protocol = CreateProtocol(
            new FakeTransport(local, peer),
            [first, second],
            options => options.MaxBatchItems = 1);
        try
        {
            var firstResponse = await protocol.ReceiveBroadcast(new DisseminationBroadcastBatch
            {
                Sender = peer,
                Values = new Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>>
                {
                    [first.Name] = [first.CreateItem(peer, "other", 1)],
                    [second.Name] = [second.CreateItem(peer, "stream", 1)],
                },
            }, TestContext.Current.CancellationToken);
            Assert.False(firstResponse.Acknowledgments.ContainsKey(second.Name));

            var changedBatch = new DisseminationBroadcastBatch
            {
                Sender = peer,
                Values = CreateValueGroups(second.Name,
                    second.CreateItem(peer, "stream", 1),
                    second.CreateItem(peer, "stream", 2, fromVersion: 1)),
            };
            var skippedPrefix = await protocol.ReceiveBroadcast(changedBatch, TestContext.Current.CancellationToken);
            Assert.Equal(0, Assert.Single(skippedPrefix.Acknowledgments[second.Name]).Version);
            Assert.Equal(0, second.GetVersion("stream"));

            var prefix = await protocol.ReceiveBroadcast(changedBatch, TestContext.Current.CancellationToken);
            Assert.Equal(1, Assert.Single(prefix.Acknowledgments[second.Name]).Version);
            var suffix = await protocol.ReceiveBroadcast(new DisseminationBroadcastBatch
            {
                Sender = peer,
                Values = CreateValueGroups(second.Name, second.CreateItem(peer, "stream", 2, fromVersion: 1)),
            }, TestContext.Current.CancellationToken);
            Assert.Equal(2, Assert.Single(suffix.Acknowledgments[second.Name]).Version);
            Assert.Equal(1, first.GetVersion("other"));
            Assert.Equal(2, second.GetVersion("stream"));
            Assert.Equal(2, second.ApplyCounts["stream"]);
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PublicationRejectsSingleValueBeyondGlobalBatchBudget()
    {
        var local = CreateSilo(39671);
        var peer = CreateSilo(39672);
        var ns = new FakeNamespace(local);
        ns.Options.MaxPayloadBytes = 16;
        var payload = new byte[9];
        BitConverter.GetBytes(1L).CopyTo(payload, 0);
        ns.PublishValue(new DisseminationValue("value", 0, 1, payload));
        var transport = new FakeTransport(local, peer);
        var protocol = CreateProtocol(transport, ns, options => options.MaxBatchBytes = 8);
        try
        {
            Assert.False(await protocol.Publish(ns, "value", 1, TestContext.Current.CancellationToken));
            await protocol.FlushPendingBroadcast(TestContext.Current.CancellationToken);
            Assert.Empty(transport.BroadcastBatches);
            Assert.Equal(0, transport.GetTargetResolutionCount(peer));
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PublicationPagesCompleteRepairWithinActualSendBudgets()
    {
        var local = CreateSilo(39681);
        var peer = CreateSilo(39682);
        var ns = new FakeNamespace(local) { ReturnRepairChain = true };
        ns.PublishValue(ns.CreateValue("chain", 1));
        ns.PublishValue(ns.CreateValue("chain", 2, fromVersion: 1));
        ns.PublishValue(ns.CreateValue("chain", 3, fromVersion: 2));
        var transport = new FakeTransport(local, peer);
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.MaxBatchItems = 1;
            options.MaxBatchBytes = 8;
        });
        try
        {
            Assert.True(await protocol.Publish(ns, "chain", 3, TestContext.Current.CancellationToken));
            await protocol.FlushPendingBroadcast(TestContext.Current.CancellationToken);
            Assert.Equal(new long[] { 1, 2, 3 },
                transport.BroadcastBatches.Select(batch => Assert.Single(GetBroadcastValues(batch.Batch)).Value.ToVersion));
            Assert.All(transport.BroadcastBatches, batch =>
                Assert.Equal(8, Assert.Single(GetBroadcastValues(batch.Batch)).Value.Payload.Length));
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
