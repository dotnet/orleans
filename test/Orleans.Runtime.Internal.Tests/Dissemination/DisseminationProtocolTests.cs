#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Xunit;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
public class DisseminationProtocolTests
{
    [Fact]
    public async Task PublishSendsGossipToDeterministicTreeChildren()
    {
        var local = CreateSilo(11111);
        var peers = Enumerable.Range(11112, 6).Select(CreateSilo).ToArray();
        var transport = new FakeTransport(local, peers);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.TreeFanout = 2);
        var item = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1);

        var result = await protocol.Publish(topic.Name, item, peers, CancellationToken.None);

        Assert.True(result);
        var expectedChildren = GetTreeChildren(local, local, peers, fanout: 2);
        Assert.Equal(expectedChildren, transport.GossipBatches.Select(batch => batch.Peer));
        Assert.All(transport.GossipBatches, batch => Assert.Equal(item.Id, batch.Batch.Items.Single().Id));
    }

    [Fact]
    public async Task PublishReturnsFalseWhenAnyParticipantIsIncapable()
    {
        var local = CreateSilo(11111);
        var peers = Enumerable.Range(11112, 6).Select(CreateSilo).ToArray();
        var transport = new FakeTransport(local, peers)
        {
            IncapablePeers = { peers[^1] },
        };

        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.TreeFanout = 2);
        var item = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1);

        var result = await protocol.Publish(topic.Name, item, peers, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(transport.GossipBatches);
    }

    [Fact]
    public async Task CapabilityProbeFailureUsesFailureBackoffInsteadOfCapabilityCache()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new TestTimeProvider();
        var probeCount = 0;
        transport.GetCapabilitiesHandler = (target, request, cancellationToken) =>
        {
            if (Interlocked.Increment(ref probeCount) == 1)
            {
                timeProvider.Advance(TimeSpan.FromSeconds(10));
                throw new InvalidOperationException("transient probe failure");
            }

            return ValueTask.FromResult(transport.CreateCapabilityResponse(target, request));
        };

        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options =>
        {
            options.CapabilityCacheTtl = TimeSpan.FromHours(1);
            options.FailureBackoff = TimeSpan.FromSeconds(5);
        }, timeProvider);
        var item = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1);

        var firstResult = await protocol.Publish(topic.Name, item, new[] { peer }, CancellationToken.None);
        var secondResult = await protocol.Publish(topic.Name, item, new[] { peer }, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var thirdResult = await protocol.Publish(topic.Name, item, new[] { peer }, CancellationToken.None);

        Assert.False(firstResult);
        Assert.False(secondResult);
        Assert.True(thirdResult);
        Assert.Equal(2, probeCount);
        Assert.Single(transport.GossipBatches);
    }

    [Fact]
    public async Task ReceiveGossipForwardsOnlyToLocalTreeChildren()
    {
        var silos = Enumerable.Range(11111, 8).Select(CreateSilo).OrderBy(GetPeerScore, StringComparer.Ordinal).ToArray();
        var root = silos[0];
        var local = silos[1];
        var peers = silos.Where(silo => !Equals(silo, local)).ToArray();
        var transport = new FakeTransport(local, peers);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.TreeFanout = 2);
        var item = topic.CreateItem(root, FakeTopic.DefaultKey, sequence: 1);

        await protocol.ReceiveGossip(new DisseminationGossipBatch
        {
            Sender = root,
            Items = new[] { item },
        }, CancellationToken.None);

        var expectedChildren = GetTreeChildren(local, root, peers, fanout: 2);
        Assert.Equal(expectedChildren, transport.GossipBatches.Select(batch => batch.Peer));
    }

    [Fact]
    public async Task DuplicateGossipDoesNotForwardAgain()
    {
        var silos = Enumerable.Range(11111, 8).Select(CreateSilo).OrderBy(GetPeerScore, StringComparer.Ordinal).ToArray();
        var root = silos[0];
        var local = silos[1];
        var peers = silos.Where(silo => !Equals(silo, local)).ToArray();
        var transport = new FakeTransport(local, peers);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.TreeFanout = 2);
        var item = topic.CreateItem(root, FakeTopic.DefaultKey, sequence: 1);
        var batch = new DisseminationGossipBatch
        {
            Sender = root,
            Items = new[] { item },
        };

        await protocol.ReceiveGossip(batch, CancellationToken.None);
        await protocol.ReceiveGossip(batch, CancellationToken.None);

        var expectedChildren = GetTreeChildren(local, root, peers, fanout: 2);
        Assert.Equal(expectedChildren.Count, transport.GossipBatches.Count);
        Assert.Equal(1, topic.ApplyCounts[item.Id.Key]);
    }

    [Fact]
    public async Task TreeRoutingInvalidatesCachedTopologyWhenActivePeersChange()
    {
        var local = CreateSilo(11111);
        var initialPeers = Enumerable.Range(11112, 4).Select(CreateSilo).ToList();
        var transport = new FakeTransport(local, initialPeers.ToArray());
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.TreeFanout = 2);
        var item = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1);

        var initialResult = await protocol.Publish(topic.Name, item, targetPeers: null, CancellationToken.None);
        var initialChildren = transport.GossipBatches.Select(batch => batch.Peer).ToArray();

        foreach (var peer in Enumerable.Range(11116, 8).Select(CreateSilo))
        {
            transport.Peers.Add(peer);
            var updatedChildren = GetTreeChildren(local, local, transport.Peers, fanout: 2);
            if (!initialChildren.SequenceEqual(updatedChildren))
            {
                transport.GossipBatches.Clear();
                var updatedItem = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 2);

                var updatedResult = await protocol.Publish(topic.Name, updatedItem, targetPeers: null, CancellationToken.None);

                Assert.True(initialResult);
                Assert.True(updatedResult);
                Assert.Equal(updatedChildren, transport.GossipBatches.Select(batch => batch.Peer));
                return;
            }
        }

        throw new InvalidOperationException("The test did not find a peer set which changes the local tree children.");
    }

    [Fact]
    public async Task AntiEntropyResponseIncludesNewerLocalValues()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic);
        topic.SetValue(FakeTopic.DefaultKey, version: 5);

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            Topics = new[]
            {
                new DisseminationCapabilityRequest
                {
                    Topic = topic.Name,
                    ProtocolVersion = topic.ProtocolVersion,
                    PayloadKinds = new[] { FakeTopic.PayloadKind },
                },
            },
            Digests = new[]
            {
                new DisseminationItemId(topic.Name, FakeTopic.DefaultKey, version: 3, FakeTopic.PayloadKind),
            },
        }, CancellationToken.None);

        var item = Assert.Single(response.Items);
        Assert.Equal(5, item.Id.Version);
        Assert.False(response.Truncated);
    }

    [Fact]
    public async Task AntiEntropyAppliesReturnedRepairItemsWithoutForwarding()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic);
        var repairItem = topic.CreateItem(peer, FakeTopic.DefaultKey, sequence: 7);
        transport.ExchangeAntiEntropyHandler = (target, request) => ValueTask.FromResult(new DisseminationAntiEntropyResponse
        {
            Sender = target,
            Items = new[] { repairItem },
        });

        var state = protocol.CreateAntiEntropyState();
        var responses = await protocol.ExchangeAntiEntropy(state, CancellationToken.None);
        await protocol.ApplyAntiEntropyResponses(responses, CancellationToken.None);

        Assert.Equal(7, topic.GetVersion(FakeTopic.DefaultKey));
        Assert.Empty(transport.GossipBatches);
        Assert.Single(transport.AntiEntropyRequests);
        Assert.Empty(transport.AntiEntropyRequests[0].Request.Digests);
        Assert.Equal(topic.Name, Assert.Single(transport.AntiEntropyRequests[0].Request.Topics).Topic);
    }

    [Fact]
    public async Task AntiEntropyAppliesValidItemsAfterFailedRepairItem()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic);
        topic.SetValue(FakeTopic.DefaultKey, version: 1);
        var badRepairItem = new DisseminationItem
        {
            Id = new DisseminationItemId(topic.Name, FakeTopic.DefaultKey, version: 2, FakeTopic.PayloadKind),
            Root = peer,
            ExpiresAt = TimeProvider.System.GetUtcNow().AddMinutes(1),
            Payload = Array.Empty<byte>(),
        };
        var goodRepairItem = topic.CreateItem(peer, FakeTopic.DefaultKey, sequence: 3);

        await protocol.ApplyAntiEntropyResponses(new[]
        {
            new DisseminationAntiEntropyResponse
            {
                Sender = peer,
                Items = new[] { badRepairItem, goodRepairItem },
            },
        }, CancellationToken.None);

        Assert.Equal(3, topic.GetVersion(FakeTopic.DefaultKey));
    }

    [Fact]
    public async Task AntiEntropyLoopContinuesAfterApplyFailure()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var service = CreateService(transport, topic, options => options.Overlay.AntiEntropyInterval = TimeSpan.FromMilliseconds(1));
        topic.SetValue(FakeTopic.DefaultKey, version: 1);

        var badRepairItem = new DisseminationItem
        {
            Id = new DisseminationItemId(topic.Name, FakeTopic.DefaultKey, version: 2, FakeTopic.PayloadKind),
            Root = peer,
            ExpiresAt = TimeProvider.System.GetUtcNow().AddMinutes(1),
            Payload = Array.Empty<byte>(),
        };
        var goodRepairItem = topic.CreateItem(peer, FakeTopic.DefaultKey, sequence: 3);
        var exchangeCount = 0;
        transport.ExchangeAntiEntropyHandler = (target, request) =>
        {
            var count = Interlocked.Increment(ref exchangeCount);
            return ValueTask.FromResult(new DisseminationAntiEntropyResponse
            {
                Sender = target,
                Items = count switch
                {
                    1 => new[] { badRepairItem },
                    2 => new[] { goodRepairItem },
                    _ => Array.Empty<DisseminationItem>(),
                },
            });
        };

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => topic.GetVersion(FakeTopic.DefaultKey) == 3);

            Assert.True(Volatile.Read(ref exchangeCount) >= 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ManifestHashIsIndependentOfDictionaryOrdering()
    {
        var manifest1 = CreateManifest(
            ("grain-b", "placement", "random"),
            ("grain-a", "placement", "local"));
        var manifest2 = CreateManifest(
            ("grain-a", "placement", "local"),
            ("grain-b", "placement", "random"));

        Assert.Equal(ManifestHashCalculator.ComputeHash(manifest1), ManifestHashCalculator.ComputeHash(manifest2));
    }

    [Fact]
    public void OptionsValidatorRejectsInvalidTreeFanout()
    {
        var options = new DisseminationOptions();
        options.Overlay.TreeFanout = 0;
        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    private static DisseminationProtocol CreateProtocol(
        FakeTransport transport,
        FakeTopic topic,
        Action<DisseminationOptions>? configure = null,
        TimeProvider? timeProvider = null)
    {
        var options = new DisseminationOptions { Enabled = true };
        configure?.Invoke(options);
        return new DisseminationProtocol(
            transport,
            new TestOptionsMonitor<DisseminationOptions>(options),
            new[] { topic },
            timeProvider ?? TimeProvider.System,
            NullLogger<DisseminationProtocol>.Instance);
    }

    private static DisseminationService CreateService(
        FakeTransport transport,
        FakeTopic topic,
        Action<DisseminationOptions>? configure = null)
    {
        var options = new DisseminationOptions { Enabled = true };
        configure?.Invoke(options);
        return new DisseminationService(
            transport,
            new TestOptionsMonitor<DisseminationOptions>(options),
            new[] { topic },
            TimeProvider.System,
            NullLogger<DisseminationProtocol>.Instance);
    }

    private static SiloAddress CreateSilo(int port) => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), port);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.True(condition(), "Condition was not satisfied within the timeout.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    private static IReadOnlyList<SiloAddress> GetTreeChildren(
        SiloAddress local,
        SiloAddress root,
        IEnumerable<SiloAddress> peers,
        int fanout)
    {
        var participants = peers
            .Append(local)
            .Append(root)
            .Distinct()
            .OrderBy(GetPeerScore, StringComparer.Ordinal)
            .ToList();
        var rootIndex = participants.FindIndex(silo => Equals(silo, root));
        if (rootIndex > 0)
        {
            participants = participants.Skip(rootIndex).Concat(participants.Take(rootIndex)).ToList();
        }

        var localIndex = participants.FindIndex(silo => Equals(silo, local));
        if (localIndex < 0)
        {
            return Array.Empty<SiloAddress>();
        }

        var firstChild = localIndex * fanout + 1;
        return Enumerable.Range(firstChild, fanout)
            .Where(index => index < participants.Count)
            .Select(index => participants[index])
            .ToArray();
    }

    private static string GetPeerScore(SiloAddress silo)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(silo.ToParsableString()));
        return Convert.ToHexString(bytes);
    }

    private static GrainManifest CreateManifest(params (string Grain, string Key, string Value)[] grains)
    {
        var grainBuilder = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
        foreach (var grain in grains)
        {
            var properties = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            properties[grain.Key] = grain.Value;
            grainBuilder[GrainType.Create(grain.Grain)] = new GrainProperties(
                properties.ToImmutable());
        }

        return new GrainManifest(
            grainBuilder.ToImmutable(),
            System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
    }

    private sealed class FakeTopic(SiloAddress localSilo) : IDisseminationTopic
    {
        public const string PayloadKind = "fake";
        public static readonly DisseminationValueKey DefaultKey = DisseminationValueKey.FromString("value");
        private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { PayloadKind };
        private readonly Dictionary<DisseminationValueKey, long> _versions = new();

        public Dictionary<DisseminationValueKey, int> ApplyCounts { get; } = new();

        public string Name => "fake-topic";

        public int ProtocolVersion => 2;

        public DisseminationTopicOptions Options { get; } = new() { Enabled = true };

        public IReadOnlySet<string> PayloadKinds => Kinds;

        public bool IsEnabled => true;

        public DisseminationItem CreateItem(SiloAddress root, DisseminationValueKey key, long sequence) => new()
        {
            Id = new DisseminationItemId(Name, key, sequence, PayloadKind),
            Root = root,
            ExpiresAt = TimeProvider.System.GetUtcNow().AddMinutes(1),
            Payload = BitConverter.GetBytes(sequence),
        };

        public void SetValue(DisseminationValueKey key, long version) => _versions[key] = version;

        public long GetVersion(DisseminationValueKey key) => _versions.TryGetValue(key, out var version) ? version : 0;

        public IReadOnlyList<DisseminationItemId> GetDigests() =>
            _versions.Select(entry => new DisseminationItemId(Name, entry.Key, entry.Value, PayloadKind)).ToArray();

        public int CompareVersion(DisseminationItemId left, DisseminationItemId right) => left.Version.CompareTo(right.Version);

        public bool IsObsolete(DisseminationItemId id) =>
            !string.Equals(id.PayloadKind, PayloadKind, StringComparison.Ordinal)
            || (_versions.TryGetValue(id.Key, out var version) && version > id.Version);

        public ValueTask<DisseminationItem?> GetItem(DisseminationItemId id, CancellationToken cancellationToken)
        {
            if (!_versions.TryGetValue(id.Key, out var version) || version < id.Version)
            {
                return ValueTask.FromResult<DisseminationItem?>(null);
            }

            return ValueTask.FromResult<DisseminationItem?>(CreateItem(localSilo, id.Key, version));
        }

        public ValueTask<DisseminationApplyResult> ApplyItem(DisseminationItem item, CancellationToken cancellationToken)
        {
            var version = BitConverter.ToInt64(item.Payload);
            if (_versions.TryGetValue(item.Id.Key, out var current))
            {
                if (current > version)
                {
                    return ValueTask.FromResult(DisseminationApplyResult.Obsolete);
                }

                if (current == version)
                {
                    return ValueTask.FromResult(DisseminationApplyResult.Duplicate);
                }
            }

            _versions[item.Id.Key] = version;
            ApplyCounts[item.Id.Key] = ApplyCounts.TryGetValue(item.Id.Key, out var count) ? count + 1 : 1;
            return ValueTask.FromResult(DisseminationApplyResult.Applied);
        }

        public ValueTask OnFallbackRequired(SiloAddress peer, DisseminationItemId id, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FakeTransport(SiloAddress localSilo, params SiloAddress[] peers) : IDisseminationTransport
    {
        private readonly List<SiloAddress> _peers = peers.ToList();

        public List<(SiloAddress Peer, DisseminationGossipBatch Batch)> GossipBatches { get; } = new();

        public List<(SiloAddress Peer, DisseminationAntiEntropyRequest Request)> AntiEntropyRequests { get; } = new();

        public List<SiloAddress> Peers => _peers;

        public HashSet<SiloAddress> IncapablePeers { get; } = new();

        public Func<SiloAddress, DisseminationCapabilityRequest, CancellationToken, ValueTask<DisseminationCapabilityResponse>>? GetCapabilitiesHandler { get; set; }

        public Func<SiloAddress, DisseminationAntiEntropyRequest, ValueTask<DisseminationAntiEntropyResponse>> ExchangeAntiEntropyHandler { get; set; } =
            static (peer, _) => ValueTask.FromResult(new DisseminationAntiEntropyResponse { Sender = peer });

        public SiloAddress LocalSilo => localSilo;

        public IReadOnlyList<SiloAddress> GetActivePeers() => _peers;

        public ValueTask<DisseminationCapabilityResponse> GetCapabilities(
            SiloAddress peer,
            DisseminationCapabilityRequest request,
            CancellationToken cancellationToken)
        {
            if (GetCapabilitiesHandler is not null)
            {
                return GetCapabilitiesHandler(peer, request, cancellationToken);
            }

            return ValueTask.FromResult(CreateCapabilityResponse(peer, request));
        }

        public DisseminationCapabilityResponse CreateCapabilityResponse(
            SiloAddress peer,
            DisseminationCapabilityRequest request) => new()
            {
                Topic = request.Topic,
                ProtocolVersion = request.ProtocolVersion,
                Supported = !IncapablePeers.Contains(peer),
                PayloadKinds = IncapablePeers.Contains(peer) ? Array.Empty<string>() : request.PayloadKinds,
            };

        public Task SendGossip(SiloAddress peer, DisseminationGossipBatch batch, CancellationToken cancellationToken)
        {
            GossipBatches.Add((peer, batch));
            return Task.CompletedTask;
        }

        public ValueTask<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(
            SiloAddress peer,
            DisseminationAntiEntropyRequest request,
            CancellationToken cancellationToken)
        {
            AntiEntropyRequests.Add((peer, request));
            return ExchangeAntiEntropyHandler(peer, request);
        }
    }

    private sealed class TestOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;

        public T Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow += value;
    }
}
