#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Fact]
    public async Task ProtocolReviewPayloadObserverFailureDoesNotBlockLaterRepair()
    {
        var local = CreateSilo(39501);
        var peer = CreateSilo(39502);
        var ns = new ProtocolReviewNamespace(local);
        ns.Inner.SetValue("oversize", 1);
        ns.Inner.SetValue("healthy", 2);
        ns.Options.MaxPayloadBytes = 8;
        ns.RepairHandler = request => request.Key == new DisseminationKey("oversize")
            ? DisseminationRepairResult.Produced(1, [new(request.Key, 0, 1, new byte[9])])
            : ns.Inner.CreateRepair(request);
        var logger = new Phase6ProtocolLogger();
        var protocol = CreatePhase6Protocol(new FakeTransport(local, peer), ns, logger);
        var observer = new ProtocolReviewPayloadObserver(local, ns.Name);
        using var subscription = DisseminationEvents.Listener.Subscribe(
            observer, static name => name == "Dissemination.PayloadDrop");
        try
        {
            var request = new DisseminationAntiEntropyRequest
            {
                Sender = peer,
                SupportedNamespaces = [ns.Name],
                Digests = CreateAntiEntropyRequestDigest(ns.Name, ("oversize", 0), ("healthy", 0)),
            };
            var response = await protocol.ReceiveAntiEntropy(request, TestContext.Current.CancellationToken);
            var value = Assert.Single(GetAntiEntropyResponseValues(response)).Value;
            Assert.Equal(new DisseminationKey("healthy"), value.Key);
            Assert.Equal(2, value.ToVersion);
            Assert.False(response.Truncated);
            Assert.Equal(1, observer.Count);
            var diagnostic = Assert.Single(logger.Entries);
            Assert.Same(observer.Failure, diagnostic.Exception);
            Assert.Equal(ns.Name, diagnostic.State["Namespace"]);
            Assert.Equal(new DisseminationKey("oversize"), diagnostic.State["Key"]);

            ns.RepairHandler = null;
            response = await protocol.ReceiveAntiEntropy(request, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "oversize", "healthy" },
                GetAntiEntropyResponseValues(response).Select(static item => item.Value.Key.ToString()));
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ProtocolReviewValueMetricFailuresDoNotBlockRepairOrApplication()
    {
        var local = CreateSilo(39503);
        var peer = CreateSilo(39504);
        var ns = new ProtocolReviewNamespace(local, "protocol-review-metric-isolation");
        ns.Inner.SetValue("oversize", 1);
        ns.Inner.SetValue("healthy", 2);
        ns.Options.MaxPayloadBytes = 8;
        ns.RepairHandler = request => request.Key == new DisseminationKey("oversize")
            ? DisseminationRepairResult.Produced(1, [new(request.Key, 0, 1, new byte[9])])
            : ns.Inner.CreateRepair(request);
        var logger = new Phase6ProtocolLogger();
        var protocol = CreatePhase6Protocol(new FakeTransport(local, peer), ns, logger);
        var failures = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == DisseminationInstruments.MeterName
                && instrument.Name is "orleans-dissemination-payload-dropped" or "orleans-dissemination-values-applied")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "namespace" && Equals(tag.Value, ns.Name))
                {
                    failures.Add(instrument.Name);
                    throw new InvalidOperationException("Metric observer failure.");
                }
            }
        });
        listener.Start();
        try
        {
            var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
            {
                Sender = peer,
                Digests = CreateAntiEntropyRequestDigest(ns.Name, ("oversize", 0), ("healthy", 0)),
            }, TestContext.Current.CancellationToken);
            Assert.Equal(new DisseminationKey("healthy"), Assert.Single(GetAntiEntropyResponseValues(response)).Value.Key);
            var acknowledgment = await protocol.ReceiveBroadcast(new DisseminationBroadcastBatch
            {
                Sender = peer,
                Values = CreateValueGroups(ns.Name, ns.Inner.CreateItem(peer, "new", 3)),
            }, TestContext.Current.CancellationToken);
            Assert.Equal(3, Assert.Single(acknowledgment.Acknowledgments[ns.Name]).Version);
            Assert.Equal(3, ns.GetVersion("new"));
            Assert.Equal(
                new[] { "orleans-dissemination-payload-dropped", "orleans-dissemination-values-applied" },
                failures);
            Assert.Equal(2, logger.Entries.Count);
            Assert.All(logger.Entries, static entry => Assert.Equal("Metric observer failure.", entry.Exception.Message));
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtocolReviewMalformedLoadPayloadRejectsAndContinues(bool antiEntropy)
    {
        var harness = CreatePhase5DeploymentLoadPublisherHarness();
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var options = new DeploymentLoadPublisherOptions();
        options.Dissemination.Enabled = true;
        var ns = new DeploymentLoadStatisticsDisseminationNamespace(
            harness.Publisher, new TestOptionsMonitor<DeploymentLoadPublisherOptions>(options), serializer);
        var statistics = CreatePhase5Statistics(1);
        var malformed = new DisseminationValue(harness.ActiveOne, 0, statistics.DateTime.Ticks, new byte[] { 0xff });
        var valid = ns.CreateValue(harness.ActiveTwo, statistics);
        var transport = new FakeTransport(harness.Local, harness.ActiveOne, harness.ActiveTwo);
        var logger = new Phase6ProtocolLogger();
        var protocol = CreatePhase6Protocol(transport, ns, logger, settings => settings.Overlay.AntiEntropyPeerCount = 1);
        using var observer = new Phase6ApplyObserver(ns.Name, malformed.Key, harness.Local, harness.ActiveOne);
        try
        {
            var values = CreateValueGroups(ns.Name,
                CreateDisseminationValue(harness.ActiveOne, malformed),
                CreateDisseminationValue(harness.ActiveOne, valid));
            if (antiEntropy)
            {
                transport.ExchangeAntiEntropyHandler = (_, _, _) => ValueTask.FromResult(
                    new DisseminationAntiEntropyResponse { Sender = harness.ActiveOne, Values = values });
                await protocol.RunAntiEntropyRound(TestContext.Current.CancellationToken);
            }
            else
            {
                var acknowledgment = await protocol.ReceiveBroadcast(
                    new DisseminationBroadcastBatch { Sender = harness.ActiveOne, Values = values },
                    TestContext.Current.CancellationToken);
                Assert.Equal(new long[] { 0, valid.ToVersion },
                    acknowledgment.Acknowledgments[ns.Name].Select(static digest => digest.Version));
            }

            Assert.Equal(0, ns.GetVersion(malformed.Key));
            Assert.Equal(valid.ToVersion, ns.GetVersion(valid.Key));
            Assert.False(harness.Publisher.PeriodicStatistics.ContainsKey(harness.ActiveOne));
            Assert.Equal(statistics.ActivationCount,
                harness.Publisher.PeriodicStatistics[harness.ActiveTwo].ActivationCount);
            Assert.Single(observer.Events);
            var failure = Assert.Single(logger.Entries);
            Assert.Equal(malformed.Key, failure.State["Key"]);
            Assert.Equal(malformed.ToVersion, failure.State["Version"]);
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtocolReviewApplicationWaitIsLocallyBounded(bool cancelCaller)
    {
        var local = CreateSilo(39511);
        var peer = CreateSilo(39512);
        var transport = new FakeTransport(local, peer);
        var clock = new FakeTimeProvider();
        var ns = new ProtocolReviewNamespace(local);
        ns.Inner.ExpectedKeys.Add("blocked");
        ns.Options.StaleItemTtl = TimeSpan.FromSeconds(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DisseminationApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken applicationToken = default;
        ns.ApplyHandler = (_, token) =>
        {
            applicationToken = token;
            entered.TrySetResult();
            return new(release.Task);
        };
        transport.ExchangeAntiEntropyHandler = (_, _, _) => ValueTask.FromResult(new DisseminationAntiEntropyResponse
        {
            Sender = peer,
            Values = CreateValueGroups(ns.Inner.CreateItem(peer, "blocked", 1)),
        });
        var protocol = CreateProtocol(transport, [ns], timeProvider: clock);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var round = protocol.RunAntiEntropyRound(caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(round.IsCompleted);
            if (cancelCaller)
            {
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => round.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            }
            else
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await round.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }

            Assert.True(applicationToken.IsCancellationRequested);
            Assert.False(release.Task.IsCompleted);
            Assert.Equal(0, ns.GetVersion("blocked"));
            ns.ApplyHandler = null;
            await protocol.RunAntiEntropyRound(TestContext.Current.CancellationToken);
            Assert.Equal(1, ns.GetVersion("blocked"));
            Assert.Equal(2, transport.AntiEntropyRequests.Count);
            Assert.Equal(0, Assert.Single(transport.AntiEntropyRequests[1].Request.Digests[ns.Name]).Version);
            release.SetResult(DisseminationApplyResult.Applied);
            Assert.Equal(1, ns.GetVersion("blocked"));
            Assert.Equal(1, ns.Inner.ApplyCounts["blocked"]);
        }
        finally
        {
            release.TrySetResult(DisseminationApplyResult.Rejected);
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ProtocolReviewSlowPeerDoesNotExpireCompletedResponseApplication()
    {
        var local = CreateSilo(39521);
        var good = CreateSilo(39522);
        var slow = CreateSilo(39523);
        var transport = new FakeTransport(local, good, slow);
        var clock = new FakeTimeProvider();
        var ns = new ProtocolReviewNamespace(local);
        ns.Options.StaleItemTtl = TimeSpan.FromSeconds(1);
        ns.Inner.ExpectedKeys.Add("value");
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource<DisseminationAntiEntropyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ns.ApplyHandler = async (value, token) =>
        {
            applyEntered.TrySetResult();
            await applyRelease.Task.WaitAsync(token);
            return await ns.Inner.ApplyValueAsync(value, token);
        };
        transport.ExchangeAntiEntropyHandler = (peer, _, _) =>
        {
            if (peer.Equals(slow))
            {
                slowEntered.TrySetResult();
                return new(slowRelease.Task);
            }

            return ValueTask.FromResult(new DisseminationAntiEntropyResponse
            {
                Sender = good,
                Values = CreateValueGroups(ns.Inner.CreateItem(good, "value", 1)),
            });
        };
        var protocol = CreateProtocol(transport, [ns], options => options.Overlay.AntiEntropyPeerCount = 2, clock);
        try
        {
            var round = protocol.RunAntiEntropyRound(TestContext.Current.CancellationToken);
            await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(1));
            await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(round.IsCompleted);
            Assert.False(slowRelease.Task.IsCompleted);
            applyRelease.SetResult();
            await round.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, ns.GetVersion("value"));
            Assert.Equal(1, ns.Inner.ApplyCounts["value"]);
        }
        finally
        {
            applyRelease.TrySetResult();
            slowRelease.TrySetResult(new DisseminationAntiEntropyResponse { Sender = slow });
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task ProtocolReviewBroadcastUsesLocalReceiptAge(int followingTtlSeconds)
    {
        var local = CreateSilo(39531);
        var peer = CreateSilo(39532);
        var clock = new FakeTimeProvider();
        var ns = new ProtocolReviewNamespace(local);
        ns.Options.StaleItemTtl = TimeSpan.FromSeconds(10);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ns.ApplyHandler = async (value, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return await ns.Inner.ApplyValueAsync(value, token);
        };
        var protocol = CreateProtocol(new FakeTransport(local, peer), [ns], timeProvider: clock);
        try
        {
            var receive = protocol.ReceiveBroadcast(CreateBroadcastBatch(peer,
                ns.Inner.CreateItem(peer, "first", 1),
                new DisseminationBroadcastValue
                {
                    Value = ns.Inner.CreateValue("later", 1),
                    TimeToLive = TimeSpan.FromSeconds(followingTtlSeconds),
                }), TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(1));
            release.SetResult();
            var response = await receive.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(new long[] { 1, 0 }, response.Acknowledgments[ns.Name].Select(static digest => digest.Version));
            Assert.Equal(new[] { new DisseminationKey("first") }, ns.Attempts.Select(static value => value.Key));
            Assert.Equal(1, ns.GetVersion("first"));
            Assert.Equal(0, ns.GetVersion("later"));
        }
        finally
        {
            release.TrySetResult();
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ProtocolReviewExpiredQueuedLoadDoesNotMutateOwnerState()
    {
        var harness = CreatePhase5DeploymentLoadPublisherHarness();
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var options = new DeploymentLoadPublisherOptions();
        options.Dissemination.Enabled = true;
        options.Dissemination.StaleItemTtl = TimeSpan.FromSeconds(1);
        var ns = new DeploymentLoadStatisticsDisseminationNamespace(
            harness.Publisher, new TestOptionsMonitor<DeploymentLoadPublisherOptions>(options),
            services.GetRequiredService<Serializer>());
        var clock = new FakeTimeProvider();
        var ownerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOwner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Publisher.SubscribeToStatisticsChangeEvents(new Phase5StatisticsListener(
            onUpdate: (silo, _) =>
            {
                if (silo.Equals(harness.Local))
                {
                    ownerEntered.TrySetResult();
                    releaseOwner.Task.GetAwaiter().GetResult();
                }
            }));
        var protocol = CreateProtocol(new FakeTransport(harness.Local, harness.ActiveOne), [ns], timeProvider: clock);
        var owner = harness.Publisher.ApplyDisseminatedRuntimeStatisticsAsync(
            harness.Local, CreatePhase5Statistics(1), TestContext.Current.CancellationToken);
        try
        {
            await ownerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var value = ns.CreateValue(harness.ActiveOne, CreatePhase5Statistics(2));
            var receive = protocol.ReceiveBroadcast(new DisseminationBroadcastBatch
            {
                Sender = harness.ActiveOne,
                Values = CreateValueGroups(ns.Name, CreateDisseminationValue(harness.ActiveOne, value)),
            }, TestContext.Current.CancellationToken);
            Assert.False(receive.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
            var response = await receive.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, Assert.Single(response.Acknowledgments[ns.Name]).Version);
            Assert.False(owner.IsCompleted);
            releaseOwner.SetResult();
            await owner.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await harness.Publisher.RunOrQueueTask(
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                }, TestContext.Current.CancellationToken);
            Assert.False(harness.Publisher.PeriodicStatistics.ContainsKey(harness.ActiveOne));
            Assert.Equal(0, ns.GetVersion(harness.ActiveOne));
        }
        finally
        {
            releaseOwner.TrySetResult();
            await owner.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(2, 1024)]
    [InlineData(10, 16)]
    [InlineData(2, 16)]
    public async Task ProtocolReviewBroadcastCapsAllNamespacesAndAcknowledgesOnlyPrefix(int maxItems, int maxBytes)
    {
        var local = CreateSilo(39541);
        var peer = CreateSilo(39542);
        var first = new ProtocolReviewNamespace(local);
        var second = new ProtocolReviewNamespace(local, "second");
        var protocol = CreateProtocol(new FakeTransport(local, peer), [first, second], options =>
        {
            options.MaxBatchItems = maxItems;
            options.MaxBatchBytes = maxBytes;
        });
        var batch = new DisseminationBroadcastBatch
        {
            Sender = peer,
            Values = new()
            {
                [first.Name] = [first.Inner.CreateItem(peer, "chain", 1), first.Inner.CreateItem(peer, "chain", 2, 1)],
                [second.Name] = [second.Inner.CreateItem(peer, "later", 3)],
            },
        };
        try
        {
            var acknowledgment = await protocol.ReceiveBroadcast(batch, TestContext.Current.CancellationToken);
            Assert.Single(acknowledgment.Acknowledgments);
            Assert.Equal(2, Assert.Single(acknowledgment.Acknowledgments[first.Name]).Version);
            Assert.Equal(new long[] { 1, 2 }, first.Attempts.Select(static value => value.ToVersion));
            Assert.Equal(16, first.Attempts.Sum(static value => value.Payload.Length));
            Assert.Empty(second.Attempts);
            Assert.Equal(0, second.GetVersion("later"));

            acknowledgment = await protocol.ReceiveBroadcast(batch, TestContext.Current.CancellationToken);
            Assert.Single(acknowledgment.Acknowledgments);
            Assert.Equal(3, Assert.Single(acknowledgment.Acknowledgments[second.Name]).Version);
            Assert.Equal(2, first.Attempts.Count);
            Assert.Single(second.Attempts);
            Assert.Equal(8, second.Attempts.Sum(static value => value.Payload.Length));
            Assert.Equal(3, second.GetVersion("later"));
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtocolReviewReceiveCursorPassesRejectedHeadAndPreservesChain(bool antiEntropy)
    {
        var local = CreateSilo(39551);
        var peer = CreateSilo(39552);
        var ns = new ProtocolReviewNamespace(local);
        ns.Inner.ExpectedKeys.UnionWith(new DisseminationKey[] { "bad", "chain" });
        ns.Options.ExpectedUpdateCadence = TimeSpan.Zero;
        var values = CreateValueGroups(
            CreateDisseminationValue(peer, new DisseminationValue("bad", 0, 1, new byte[9])),
            ns.Inner.CreateItem(peer, "chain", 1),
            ns.Inner.CreateItem(peer, "chain", 2, 1));
        var transport = new FakeTransport(local, peer);
        transport.ExchangeAntiEntropyHandler = (_, _, _) => ValueTask.FromResult(
            new DisseminationAntiEntropyResponse { Sender = peer, Values = values });
        var protocol = CreateProtocol(transport, [ns], options =>
        {
            options.MaxBatchItems = 1;
            options.MaxBatchBytes = 8;
        });
        try
        {
            for (var round = 0; round < 3; round++)
            {
                if (antiEntropy)
                {
                    await protocol.RunAntiEntropyRound(TestContext.Current.CancellationToken);
                }
                else
                {
                    var response = await protocol.ReceiveBroadcast(
                        new DisseminationBroadcastBatch { Sender = peer, Values = values },
                        TestContext.Current.CancellationToken);
                    if (round == 0)
                    {
                        Assert.Empty(response.Acknowledgments);
                    }
                    else
                    {
                        Assert.Equal(round, Assert.Single(response.Acknowledgments[ns.Name]).Version);
                    }
                }

                Assert.Equal(round, ns.GetVersion("chain"));
            }

            Assert.Equal(0, ns.GetVersion("bad"));
            Assert.Equal(new long[] { 1, 2 }, ns.Attempts.Select(static value => value.ToVersion));
            Assert.Equal(new long[] { 0, 1 }, ns.Attempts.Select(static value => value.FromVersion));
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(2, 1024)]
    [InlineData(10, 16)]
    [InlineData(2, 16)]
    public async Task ProtocolReviewAntiEntropyCapsEachResponseBeforeGrouping(int maxItems, int maxBytes)
    {
        var local = CreateSilo(39561);
        var badPeer = CreateSilo(39562);
        var goodPeer = CreateSilo(39563);
        var ns = new ProtocolReviewNamespace(local);
        var other = new ProtocolReviewNamespace(local, "other");
        var transport = new FakeTransport(local, badPeer, goodPeer);
        transport.ExchangeAntiEntropyHandler = (peer, _, _) =>
        {
            var first = peer.Equals(badPeer)
                ? CreateDisseminationValue(peer, new DisseminationValue("chain", 0, 1, BitConverter.GetBytes(99L)))
                : ns.Inner.CreateItem(peer, "chain", 1);
            return ValueTask.FromResult(new DisseminationAntiEntropyResponse
            {
                Sender = peer,
                Values = new()
                {
                    [ns.Name] = [first, ns.Inner.CreateItem(peer, "chain", 2, 1)],
                    [other.Name] = [other.Inner.CreateItem(peer, "omitted", 3)],
                },
            });
        };
        var protocol = CreateProtocol(transport, [ns, other], options =>
        {
            options.Overlay.AntiEntropyPeerCount = 2;
            options.MaxBatchItems = maxItems;
            options.MaxBatchBytes = maxBytes;
        });
        try
        {
            await protocol.RunAntiEntropyRound(TestContext.Current.CancellationToken);
            Assert.Equal(2, transport.AntiEntropyRequests.Count);
            Assert.Equal(2, ns.GetVersion("chain"));
            Assert.Equal(new long[] { 1, 1, 2 }, ns.Attempts.Select(static value => value.ToVersion));
            Assert.Equal(24, ns.Attempts.Sum(static value => value.Payload.Length));
            Assert.Equal(2, ns.Inner.ApplyCounts["chain"]);
            Assert.Empty(other.Attempts);
            Assert.Equal(0, other.GetVersion("omitted"));

            await protocol.RunAntiEntropyRound(TestContext.Current.CancellationToken);
            Assert.Equal(3, other.GetVersion("omitted"));
            Assert.Equal(2, other.Attempts.Count);
            Assert.Equal(16, other.Attempts.Sum(static value => value.Payload.Length));
            Assert.Equal(3, ns.Attempts.Count);
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtocolReviewLargerResponderOptionsRepairThroughLocalCaps(bool ignoresBudgets)
    {
        var local = CreateSilo(39571);
        var peer = CreateSilo(39572);
        var receiver = new ProtocolReviewNamespace(local);
        var source = new FakeNamespace(peer);
        DisseminationKey[] keys = ["first", "second", "last"];
        receiver.Inner.ExpectedKeys.UnionWith(keys);
        foreach (var key in keys)
        {
            source.SetValue(key, 1);
        }

        var responses = new List<DisseminationAntiEntropyResponse>();
        var remote = CreateProtocol(new FakeTransport(peer, local), source, options =>
        {
            options.MaxBatchItems = 10;
            options.MaxBatchBytes = 1024;
        });
        var transport = new FakeTransport(local, peer);
        transport.ExchangeAntiEntropyHandler = async (_, request, token) =>
        {
            Assert.Equal(2, request.MaxResponseItems);
            Assert.Equal(16, request.MaxResponseBytes);
            if (ignoresBudgets)
            {
                request = new DisseminationAntiEntropyRequest
                {
                    Sender = request.Sender,
                    Digests = request.Digests,
                    SupportedNamespaces = request.SupportedNamespaces,
                };
            }

            var response = await remote.ReceiveAntiEntropy(request, token);
            responses.Add(response);
            return response;
        };
        var protocol = CreateProtocol(transport, [receiver], options =>
        {
            options.MaxBatchItems = 2;
            options.MaxBatchBytes = 16;
        });
        try
        {
            await protocol.RunAntiEntropyRound(TestContext.Current.CancellationToken);
            Assert.Equal(ignoresBudgets ? 3 : 2, GetAntiEntropyResponseValues(Assert.Single(responses)).Count());
            Assert.Equal(ignoresBudgets ? 24 : 16, GetAntiEntropyResponseValues(responses[0]).Sum(static item => item.Value.Payload.Length));
            Assert.Equal(keys.Take(2), receiver.Attempts.Select(static value => value.Key));
            Assert.Equal(16, receiver.Attempts.Sum(static value => value.Payload.Length));
            Assert.Equal(0, receiver.GetVersion(keys[2]));
            await protocol.RunAntiEntropyRound(TestContext.Current.CancellationToken);
            Assert.Equal(keys[2], Assert.Single(GetAntiEntropyResponseValues(responses[1])).Value.Key);
            Assert.Equal(keys, receiver.Attempts.Select(static value => value.Key));
            Assert.All(keys, key => Assert.Equal(1, receiver.GetVersion(key)));
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
            await remote.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ProtocolReviewAdvancingInventoryDoesNotAliasResponderAndReceiverCursors()
    {
        var local = CreateSilo(39591);
        var peer = CreateSilo(39592);
        var clock = new FakeTimeProvider();
        var receiver = new ProtocolReviewNamespace(local);
        receiver.Options.ExpectedUpdateCadence = TimeSpan.FromSeconds(1);
        var source = new FakeNamespace(peer);
        DisseminationKey[] keys = ["A", "B", "C", "D"];
        receiver.Inner.ExpectedKeys.UnionWith(keys);
        var responses = new List<DisseminationAntiEntropyResponse>();
        var remote = CreateProtocol(new FakeTransport(peer, local), source, options =>
        {
            options.MaxBatchItems = 2;
            options.MaxBatchBytes = 16;
        }, clock);
        var transport = new FakeTransport(local, peer);
        transport.ExchangeAntiEntropyHandler = async (_, request, token) =>
        {
            var response = await remote.ReceiveAntiEntropy(request, token);
            responses.Add(response);
            return response;
        };
        var protocol = CreateProtocol(transport, [receiver], options =>
        {
            options.MaxBatchItems = 1;
            options.MaxBatchBytes = 8;
        }, clock);
        try
        {
            for (var round = 1; round <= keys.Length; round++)
            {
                foreach (var key in keys)
                {
                    source.SetValue(key, round);
                }

                clock.Advance(TimeSpan.FromSeconds(2));
                await protocol.RunAntiEntropyRound(TestContext.Current.CancellationToken);
            }

            Assert.Equal(keys, receiver.Attempts.Select(static value => value.Key));
            Assert.Equal(new long[] { 1, 2, 3, 4 }, keys.Select(receiver.GetVersion));
            Assert.Equal(4, responses.Count);
            Assert.Equal(keys, responses.Select(static response => Assert.Single(GetAntiEntropyResponseValues(response)).Value.Key));
            Assert.All(transport.AntiEntropyRequests, request =>
            {
                Assert.Equal(1, request.Request.MaxResponseItems);
                Assert.Equal(8, request.Request.MaxResponseBytes);
                Assert.Equal(4, request.Request.Digests[receiver.Name].Count);
            });
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
            await remote.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(0, null, "MaxResponseItems")]
    [InlineData(-1, null, "MaxResponseItems")]
    [InlineData(null, 0, "MaxResponseBytes")]
    [InlineData(null, -1, "MaxResponseBytes")]
    public async Task ProtocolReviewInvalidResponseBudgetsHaveNoReceiveSideEffects(
        int? maxResponseItems,
        int? maxResponseBytes,
        string parameterName)
    {
        var local = CreateSilo(39601);
        var peer = CreateSilo(39602);
        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromHours(1);
        ns.SetValue("pending", 1);
        var transport = new FakeTransport(local, peer);
        var protocol = CreateProtocol(transport, ns, timeProvider: new FakeTimeProvider());
        try
        {
            Assert.True(await protocol.Publish(ns, "pending", 1, TestContext.Current.CancellationToken));
            var repairRequests = ns.RepairRequestCount;
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                _ = protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
                {
                    Sender = peer,
                    SupportedNamespaces = [ns.Name],
                    Digests = CreateAntiEntropyRequestDigest(ns.Name, ("pending", 1)),
                    MaxResponseItems = maxResponseItems,
                    MaxResponseBytes = maxResponseBytes,
                }, TestContext.Current.CancellationToken);
            });
            Assert.Equal(parameterName, exception.ParamName);
            Assert.Equal(maxResponseItems ?? maxResponseBytes, Assert.IsType<int>(exception.ActualValue));
            Assert.Equal(repairRequests, ns.RepairRequestCount);
            Assert.Equal(peer, Assert.Single(protocol.GetUnconfirmedPeers(ns)));

            await protocol.FlushPendingBroadcast(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var sent = Assert.Single(transport.BroadcastBatches);
            Assert.Equal(1, Assert.Single(GetBroadcastValues(sent.Batch)).Value.ToVersion);
            Assert.Equal(peer, sent.Peer);
        }
        finally
        {
            ns.Options.Enabled = false;
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1, null)]
    [InlineData(null, 8)]
    [InlineData(1, 8)]
    [InlineData(10, 1024)]
    public async Task ProtocolReviewResponseBudgetsRoundTripAndBoundEveryRepairProbe(int? maxResponseItems, int? maxResponseBytes)
    {
        var local = CreateSilo(39611);
        var peer = CreateSilo(39612);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var ns = new ProtocolReviewNamespace(local);
        DisseminationKey[] keys = ["A", "B", "C"];
        foreach (var key in keys)
        {
            ns.Inner.SetValue(key, 1);
        }

        var requests = new List<DisseminationRepairRequest>();
        ns.RepairHandler = request =>
        {
            requests.Add(request);
            return ns.Inner.CreateRepair(request);
        };
        var request = new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            SupportedNamespaces = [ns.Name],
            Digests = CreateAntiEntropyRequestDigest(ns.Name, ("A", 0), ("B", 0), ("C", 0)),
            MaxResponseItems = maxResponseItems,
            MaxResponseBytes = maxResponseBytes,
        };
        var copy = Assert.IsType<DisseminationAntiEntropyRequest>(
            serializer.Deserialize<DisseminationAntiEntropyRequest>(serializer.SerializeToArray(request)));
        Assert.Equal(maxResponseItems, copy.MaxResponseItems);
        Assert.Equal(maxResponseBytes, copy.MaxResponseBytes);
        Assert.Equal(peer, copy.Sender);
        Assert.Equal(ns.Name, Assert.Single(copy.SupportedNamespaces));
        Assert.Equal(keys, copy.Digests[ns.Name].Select(static digest => digest.Key));
        var protocol = CreateProtocol(new FakeTransport(local, peer), [ns], options =>
        {
            options.MaxBatchItems = 2;
            options.MaxBatchBytes = 16;
        });
        try
        {
            var response = await protocol.ReceiveAntiEntropy(copy, TestContext.Current.CancellationToken);
            var maxItems = Math.Min(2, maxResponseItems ?? int.MaxValue);
            var maxBytes = Math.Min(16, maxResponseBytes ?? int.MaxValue);
            var count = Math.Min(maxItems, maxBytes / sizeof(long));
            Assert.Equal(keys.Take(count), GetAntiEntropyResponseValues(response).Select(static item => item.Value.Key));
            Assert.Equal(count * sizeof(long), GetAntiEntropyResponseValues(response).Sum(static item => item.Value.Payload.Length));
            Assert.True(response.Truncated);
            Assert.Equal(count + 2, requests.Count);
            for (var index = 0; index <= count; index++)
            {
                Assert.Equal(keys[index], requests[index].Key);
                Assert.Equal(maxItems - index, requests[index].MaxItemCount);
                Assert.Equal(maxBytes - index * sizeof(long), requests[index].MaxBatchBytes);
            }

            Assert.Equal(keys[count], requests[^1].Key);
            Assert.Equal(maxItems, requests[^1].MaxItemCount);
            Assert.Equal(maxBytes, requests[^1].MaxBatchBytes);
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ProtocolReviewLargerBroadcastSenderContinuesFromAcknowledgedPrefix()
    {
        var sourceAddress = CreateSilo(39581);
        var receiverAddress = CreateSilo(39582);
        var clock = new FakeTimeProvider();
        var source = new FakeNamespace(sourceAddress) { ReturnRepairChain = true };
        source.Options.MaxCoalescingDelay = TimeSpan.FromHours(1);
        source.PublishValue(source.CreateValue("chain", 1));
        source.PublishValue(source.CreateValue("chain", 2, 1));
        source.PublishValue(source.CreateValue("chain", 3, 2));
        var receiver = new ProtocolReviewNamespace(receiverAddress);
        var receivingProtocol = CreateProtocol(new FakeTransport(receiverAddress, sourceAddress), [receiver], options =>
        {
            options.MaxBatchItems = 1;
            options.MaxBatchBytes = 8;
        }, clock);
        var acknowledgments = new List<long>();
        var sentItemCounts = new List<int>();
        var transport = new FakeTransport(sourceAddress, receiverAddress);
        transport.SendBroadcastResponseHandler = async (_, batch, token) =>
        {
            sentItemCounts.Add(GetBroadcastValues(batch).Count());
            var acknowledgment = await receivingProtocol.ReceiveBroadcast(batch, token);
            acknowledgments.Add(Assert.Single(acknowledgment.Acknowledgments[source.Name]).Version);
            return acknowledgment;
        };
        var sendingProtocol = CreateProtocol(transport, source, options =>
        {
            options.MaxBatchItems = 10;
            options.MaxBatchBytes = 1024;
        }, clock);
        try
        {
            Assert.True(await sendingProtocol.Publish(source, "chain", 3, TestContext.Current.CancellationToken));
            await sendingProtocol.FlushPendingBroadcast(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(new long[] { 1, 2, 3 }, acknowledgments);
            Assert.Equal(new[] { 3, 2, 1 }, sentItemCounts);
            Assert.Equal(new long[] { 0, 1, 2 }, receiver.Attempts.Select(static value => value.FromVersion));
            Assert.Equal(new long[] { 1, 2, 3 }, receiver.Attempts.Select(static value => value.ToVersion));
            Assert.Equal(3, receiver.GetVersion("chain"));
        }
        finally
        {
            await sendingProtocol.StopAsync(TestContext.Current.CancellationToken);
            await receivingProtocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class ProtocolReviewNamespace(SiloAddress local, DisseminationNamespace? name = null) : IDisseminationNamespace
    {
        public FakeNamespace Inner { get; } = new(local, name);
        public DisseminationNamespace Name => Inner.Name;
        public DisseminationNamespaceOptions Options => Inner.Options;
        public IEnumerable<DigestEntry> Digests => Inner.Digests;
        public List<DisseminationValue> Attempts { get; } = [];
        public Func<DisseminationValue, CancellationToken, ValueTask<DisseminationApplyResult>>? ApplyHandler { get; set; }
        public Func<DisseminationRepairRequest, DisseminationRepairResult>? RepairHandler { get; set; }
        public long GetVersion(DisseminationKey key) => Inner.GetVersion(key);
        public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request) =>
            RepairHandler is { } handler ? handler(request) : Inner.CreateRepair(request);

        public ValueTask<DisseminationApplyResult> ApplyValueAsync(DisseminationValue value, CancellationToken cancellationToken)
        {
            Attempts.Add(value);
            return ApplyHandler is { } handler ? handler(value, cancellationToken) : Inner.ApplyValueAsync(value, cancellationToken);
        }
    }

    private sealed class ProtocolReviewPayloadObserver(SiloAddress local, DisseminationNamespace namespaceName) : IObserver<KeyValuePair<string, object?>>
    {
        public Exception Failure { get; } = new InvalidOperationException("Payload observer failure.");
        public int Count { get; private set; }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is DisseminationValueEvent dropped
                && dropped.Namespace == namespaceName
                && Equals(dropped.LocalSilo, local))
            {
                Count++;
                throw Failure;
            }
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
