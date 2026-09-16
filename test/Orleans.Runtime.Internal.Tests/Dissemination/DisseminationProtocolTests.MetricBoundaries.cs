using System.Diagnostics.Metrics;
using Orleans.Runtime.Dissemination;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Theory]
    [InlineData("broadcast-receive")]
    [InlineData("broadcast-send")]
    [InlineData("repair-receive")]
    [InlineData("repair-send")]
    [InlineData("publication")]
    [InlineData("repair-failure")]
    [InlineData("broadcast-failure")]
    public async Task MetricFailuresPreserveProtocolResults(string operation)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(41021);
        var peer = CreateSilo(41022);
        var healthy = CreateSilo(41023);
        var transport = operation == "repair-failure" ? new FakeTransport(local, peer, healthy) : new FakeTransport(local, peer);
        var ns = new FakeNamespace(local, new DisseminationNamespace("metric-boundary-" + operation));
        ns.Options.MaxCoalescingDelay = TimeSpan.FromHours(1);
        var logger = new Phase6ProtocolLogger();
        var protocol = CreatePhase6Protocol(transport, ns, logger, options => options.Overlay.AntiEntropyPeerCount = 2);
        var metric = operation switch
        {
            "broadcast-receive" => "orleans-dissemination-broadcast-received",
            "broadcast-send" => "orleans-dissemination-broadcast-sent",
            "publication" => DisseminationInstruments.PublicationsName,
            "repair-failure" => DisseminationInstruments.AntiEntropyFailuresName,
            "broadcast-failure" => DisseminationInstruments.BroadcastSendFailuresName,
            _ => "orleans-dissemination-anti-entropy-exchanges",
        };
        var failure = new InvalidOperationException("Metric observer failure.");
        var failures = 0;
        var listenerArmed = 1;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (Volatile.Read(ref listenerArmed) != 0
                && instrument.Meter.Name == DisseminationInstruments.MeterName && instrument.Name == metric)
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (Volatile.Read(ref listenerArmed) == 0)
            {
                return;
            }

            if (operation is "broadcast-receive" or "broadcast-send" or "publication")
            {
                var matches = false;
                foreach (var tag in tags)
                {
                    matches |= tag.Key == "namespace" && Equals(tag.Value, ns.Name);
                }
                if (!matches)
                {
                    return;
                }
            }

            Interlocked.Increment(ref failures);
            throw failure;
        });
        listener.Start();
        try
        {
            switch (operation)
            {
                case "broadcast-receive":
                {
                    var response = await protocol.ReceiveBroadcast(new()
                    {
                        Sender = peer,
                        Values = CreateValueGroups(ns.Name, ns.CreateItem(peer, "value", 2)),
                    }, cancellationToken);
                    Assert.Equal(2, ns.GetVersion("value"));
                    Assert.Equal(2, Assert.Single(response.Acknowledgments[ns.Name]).Version);
                    break;
                }
                case "broadcast-send":
                case "publication":
                    ns.SetValue("value", 1);
                    Assert.True(await protocol.Publish(ns, "value", 1, cancellationToken));
                    await protocol.FlushPendingBroadcast(cancellationToken);
                    Assert.Single(transport.BroadcastBatches);
                    await protocol.FlushPendingBroadcast(cancellationToken);
                    Assert.Single(transport.BroadcastBatches);
                    break;
                case "repair-receive":
                {
                    ns.SetValue("value", 2);
                    var response = await protocol.ReceiveAntiEntropy(new()
                    {
                        Sender = peer,
                        SupportedNamespaces = [ns.Name],
                        Digests = new() { [ns.Name] = [new("value", 1)] },
                    }, cancellationToken);
                    Assert.Equal(2, Assert.Single(GetAntiEntropyResponseValues(response)).Value.ToVersion);
                    break;
                }
                case "repair-send":
                case "repair-failure":
                    ns.SetValue("value", 1);
                    transport.ExchangeAntiEntropyHandler = (target, _, _) =>
                        operation == "repair-failure" && target.Equals(peer)
                            ? ValueTask.FromException<DisseminationAntiEntropyResponse>(new InvalidOperationException("Transport failure."))
                            : ValueTask.FromResult(new DisseminationAntiEntropyResponse
                            {
                                Sender = target,
                                Values = CreateValueGroups(ns.Name, ns.CreateItem(target, "value", 2)),
                            });
                    await protocol.RunAntiEntropyRound(cancellationToken);
                    Assert.Equal(2, ns.GetVersion("value"));
                    break;
                case "broadcast-failure":
                    ns.SetValue("value", 1);
                    transport.SendBroadcastResponseHandler = (target, batch, _) =>
                    {
                        transport.BroadcastBatches.Add((target, batch));
                        return transport.BroadcastBatches.Count == 1
                            ? Task.FromException<DisseminationBroadcastResponse>(new InvalidOperationException("Transport failure."))
                            : Task.FromResult(FakeTransport.CreateAcknowledgment(batch));
                    };
                    Assert.True(await protocol.Publish(ns, "value", 1, cancellationToken));
                    await protocol.FlushPendingBroadcast(cancellationToken);
                    await protocol.FlushPendingBroadcast(cancellationToken);
                    Assert.Equal(2, transport.BroadcastBatches.Count);
                    Assert.All(transport.BroadcastBatches, batch =>
                        Assert.Equal(1, Assert.Single(GetBroadcastValues(batch.Batch)).Value.ToVersion));
                    await protocol.FlushPendingBroadcast(cancellationToken);
                    Assert.Equal(2, transport.BroadcastBatches.Count);
                    break;
                default:
                    throw new InvalidOperationException(operation);
            }

            Assert.Equal(1, Volatile.Read(ref failures));
            if (operation is not ("broadcast-send" or "broadcast-failure"))
            {
                Assert.Contains(logger.Entries, entry => ReferenceEquals(entry.Exception, failure));
            }
        }
        finally
        {
            Volatile.Write(ref listenerArmed, 0);
            listener.Dispose();
            ns.Options.Enabled = false;
            await protocol.StopAsync(cancellationToken);
        }
    }
}
