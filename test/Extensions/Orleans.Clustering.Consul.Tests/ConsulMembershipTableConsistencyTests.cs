using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Membership;
using Xunit;

namespace Consul.Tests;

[TestCategory("BVT"), TestCategory("Consul")]
[TestSuite("BVT"), TestProvider("Consul"), TestArea("Membership")]
public class ConsulMembershipTableConsistencyTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("membership/nested")]
    public async Task ReadsReturnOneConsistentClusterSnapshot(string? root)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable("cluster", root);
        var neighbor = store.CreateTable("cluster-other", root);
        var entry = Entry();
        await table.InitializeMembershipTableAsync(true, CancellationToken);
        await table.InitializeMembershipTableAsync(true, CancellationToken);
        var initial = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(0, initial.Version.Version);
        Assert.NotEqual("0", initial.Version.VersionEtag);
        Assert.True(await table.InsertRowAsync(entry, initial.Version.Next(), CancellationToken));
        Assert.True(await neighbor.InsertRowAsync(Entry(2), new TableVersion(1, "0"), CancellationToken));

        var requests = store.RequestCount;
        var all = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(requests + 1, store.RequestCount);
        Assert.Equal(entry.SiloAddress, Assert.Single(all.Members).Item1.SiloAddress);
        Assert.Equal(entry.IAmAliveTime, all.Members[0].Item1.IAmAliveTime);
        Assert.Equal(1, all.Version.Version);

        requests = store.RequestCount;
        var row = await table.ReadRowAsync(entry.SiloAddress, CancellationToken);
        Assert.Equal(requests + 1, store.RequestCount);
        Assert.Equal(all.Version, row.Version);
        Assert.Equal(all.Members[0].Item2, Assert.Single(row.Members).Item2);
        var missing = await table.ReadRowAsync(Entry(3).SiloAddress, CancellationToken);
        Assert.Empty(missing.Members);
        Assert.Equal(all.Version, missing.Version);
        var gatewayView = await ConsulBasedMembershipTable.ReadAllAsync(
            store.Client, "cluster", root, NullLogger.Instance, null, CancellationToken);
        Assert.Equal(all.Version, gatewayView.Version);
        Assert.Equal(entry.SiloAddress, Assert.Single(gatewayView.Members).Item1.SiloAddress);
    }

    [Fact]
    public async Task InsertAndUpdateAtomicallyRejectStaleRowOrTableTokens()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var otherWriter = store.CreateTable();
        var entry = Entry();
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), CancellationToken));
        var first = await table.ReadAllAsync(CancellationToken);
        var original = Assert.Single(first.Members);
        var before = store.Snapshot();
        Assert.False(await table.InsertRowAsync(entry, first.Version.Next(), CancellationToken));
        Assert.False(await table.InsertRowAsync(Entry(2), new TableVersion(1, "0"), CancellationToken));
        Assert.Equal(before, store.Snapshot());

        entry.Status = SiloStatus.ShuttingDown;
        store.BeforeTransaction = async () =>
        {
            Assert.True(await otherWriter.InsertRowAsync(Entry(2), first.Version.Next(), CancellationToken));
        };
        Assert.False(await table.UpdateRowAsync(entry, original.Item2, first.Version.Next(), CancellationToken));
        var after = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(2, after.Members.Count);
        Assert.Equal(2, after.Version.Version);
        var unchanged = after.Members.Single(row => row.Item1.SiloAddress.Equals(entry.SiloAddress));
        Assert.Equal(SiloStatus.Active, unchanged.Item1.Status);
        Assert.Equal(original.Item2, unchanged.Item2);
        Assert.Equal(original.Item1.IAmAliveTime, unchanged.Item1.IAmAliveTime);

        Assert.True(await table.UpdateRowAsync(entry, original.Item2, after.Version.Next(), CancellationToken));
        var updated = await table.ReadAllAsync(CancellationToken);
        before = store.Snapshot();
        Assert.False(await table.UpdateRowAsync(entry, original.Item2, updated.Version.Next(), CancellationToken));
        Assert.False(await table.UpdateRowAsync(Entry(3), "0", updated.Version.Next(), CancellationToken));
        Assert.Equal(before, store.Snapshot());
        Assert.Equal(3, updated.Version.Version);
        var changed = updated.Members.Single(row => row.Item1.SiloAddress.Equals(entry.SiloAddress));
        Assert.Equal(SiloStatus.ShuttingDown, changed.Item1.Status);
        Assert.NotEqual(original.Item2, changed.Item2);
    }

    [Fact]
    public async Task HeartbeatAndStatusRacesPreserveMaximumTimestamp()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var otherWriter = store.CreateTable();
        var entry = Entry();
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), CancellationToken));
        var first = await table.ReadAllAsync(CancellationToken);
        var original = Assert.Single(first.Members);
        var later = Entry();
        later.IAmAliveTime = Epoch.AddHours(4);
        store.BeforeTransaction = () => otherWriter.UpdateIAmAliveAsync(later, CancellationToken);
        entry.IAmAliveTime = Epoch.AddHours(2);
        await table.UpdateIAmAliveAsync(entry, CancellationToken);
        var heartbeatOnly = await table.ReadRowAsync(entry.SiloAddress, CancellationToken);
        Assert.Equal(later.IAmAliveTime, Assert.Single(heartbeatOnly.Members).Item1.IAmAliveTime);
        Assert.Equal(original.Item2, heartbeatOnly.Members[0].Item2);
        Assert.Equal(first.Version, heartbeatOnly.Version);
        Assert.Equal(1, store.ConflictCount);

        later.IAmAliveTime = Epoch.AddHours(6);
        store.BeforeTransaction = () => otherWriter.UpdateIAmAliveAsync(later, CancellationToken);
        entry.Status = SiloStatus.ShuttingDown;
        Assert.True(await table.UpdateRowAsync(entry, original.Item2, first.Version.Next(), CancellationToken));
        var updated = await table.ReadAllAsync(CancellationToken);
        var row = Assert.Single(updated.Members);
        Assert.Equal(later.IAmAliveTime, row.Item1.IAmAliveTime);
        Assert.Equal(SiloStatus.ShuttingDown, row.Item1.Status);
        Assert.Equal(2, updated.Version.Version);
        Assert.Equal(2, store.ConflictCount);

        entry.IAmAliveTime = Epoch.AddHours(8);
        Assert.True(await table.UpdateRowAsync(entry, row.Item2, updated.Version.Next(), CancellationToken));
        await table.UpdateIAmAliveAsync(later, CancellationToken);
        updated = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(entry.IAmAliveTime, Assert.Single(updated.Members).Item1.IAmAliveTime);
        Assert.Equal(3, updated.Version.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusUpdateRacingRowChangeOrPruneIsRejected(bool prune)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var otherWriter = store.CreateTable();
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), CancellationToken));
        var first = await table.ReadAllAsync(CancellationToken);
        var original = Assert.Single(first.Members);
        string? committed = null;
        store.BeforeTransaction = async () =>
        {
            if (prune)
            {
                await otherWriter.CleanupDefunctSiloEntriesAsync(Epoch.AddDays(1), CancellationToken);
            }
            else
            {
                var winner = Entry();
                winner.IAmAliveTime = Epoch.AddHours(3);
                Assert.True(await otherWriter.UpdateRowAsync(winner, original.Item2, first.Version.Next(), CancellationToken));
            }

            committed = store.Snapshot();
        };
        entry.Status = SiloStatus.ShuttingDown;
        Assert.False(await table.UpdateRowAsync(entry, original.Item2, first.Version.Next(), CancellationToken));
        Assert.Equal(committed, store.Snapshot());
        Assert.Equal(1, store.ConflictCount);
        var result = await table.ReadAllAsync(CancellationToken);
        if (prune)
        {
            Assert.Empty(result.Members);
            Assert.Equal(first.Version, result.Version);
        }
        else
        {
            var winner = Assert.Single(result.Members);
            Assert.Equal(SiloStatus.Active, winner.Item1.Status);
            Assert.Equal(Epoch.AddHours(3), winner.Item1.IAmAliveTime);
            Assert.Equal(2, result.Version.Version);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task HeartbeatRacingStatusUpdatePreservesStatusAndMaximum(int statusHeartbeatHour)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var otherWriter = store.CreateTable();
        var entry = Entry();
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), CancellationToken));
        var first = await table.ReadAllAsync(CancellationToken);
        MembershipTableData? committed = null;
        store.BeforeTransaction = async () =>
        {
            var statusUpdate = Entry();
            statusUpdate.Status = SiloStatus.ShuttingDown;
            statusUpdate.IAmAliveTime = Epoch.AddHours(statusHeartbeatHour);
            Assert.True(await otherWriter.UpdateRowAsync(statusUpdate, Assert.Single(first.Members).Item2, first.Version.Next(), CancellationToken));
            committed = await otherWriter.ReadAllAsync(CancellationToken);
        };
        entry.IAmAliveTime = Epoch.AddHours(4);
        await table.UpdateIAmAliveAsync(entry, CancellationToken);
        var result = await table.ReadAllAsync(CancellationToken);
        Assert.NotNull(committed);
        Assert.Equal(committed.Version, result.Version);
        var row = Assert.Single(result.Members);
        Assert.Equal(Assert.Single(committed.Members).Item2, row.Item2);
        Assert.Equal(SiloStatus.ShuttingDown, row.Item1.Status);
        Assert.Equal(Epoch.AddHours(Math.Max(4, statusHeartbeatHour)), row.Item1.IAmAliveTime);
        Assert.Equal(1, store.ConflictCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupPrunesOnlyOldDeadRowsWithoutChangingVersion(bool legacy)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var cutoff = Epoch.AddDays(1);
        var expected = new List<SiloAddress>();
        var id = 0;
        foreach (var status in Enum.GetValues<SiloStatus>())
        {
            var entry = Entry(++id);
            entry.Status = status;
            store.Seed(entry, heartbeat: !legacy);
            if (status != SiloStatus.Dead)
            {
                expected.Add(entry.SiloAddress);
            }
        }

        foreach (var field in new[] { "start", "heartbeat", "vote" })
        {
            foreach (var offset in new[] { -1, 0, 1 })
            {
                var entry = Entry(++id);
                entry.Status = SiloStatus.Dead;
                var time = cutoff.AddTicks(offset);
                if (field == "start")
                {
                    entry.StartTime = time;
                }
                else if (field == "heartbeat")
                {
                    entry.IAmAliveTime = time;
                }
                else
                {
                    entry.SuspectTimes = new() { Tuple.Create(Entry(100).SiloAddress, time) };
                }

                store.Seed(entry, heartbeat: field == "heartbeat" || !legacy);
                if (offset >= 0)
                {
                    expected.Add(entry.SiloAddress);
                }
            }
        }

        if (!legacy)
        {
            await table.InitializeMembershipTableAsync(true, CancellationToken);
        }

        var version = (await table.ReadAllAsync(CancellationToken)).Version;
        var transactionStart = store.Transactions.Count;
        await table.CleanupDefunctSiloEntriesAsync(new DateTimeOffset(cutoff).ToOffset(TimeSpan.FromHours(3)), CancellationToken);
        var result = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(version, result.Version);
        Assert.Equal(expected.OrderBy(address => address.ToParsableString()),
            result.Members.Select(row => row.Item1.SiloAddress).OrderBy(address => address.ToParsableString()));
        Assert.All(store.Transactions.Skip(transactionStart), operations =>
        {
            Assert.Equal(2, operations.Count);
            Assert.All(operations, operation => Assert.Equal(KVTxnVerb.DeleteCAS, operation.Verb));
            Assert.DoesNotContain(operations, operation => operation.Key.EndsWith("/version", StringComparison.Ordinal));
        });
        Assert.All(store.Keys.Where(key => key.EndsWith("/iamalive", StringComparison.Ordinal)),
            key => Assert.Contains(key[..^"/iamalive".Length], store.Keys));
    }

    [Theory]
    [InlineData("heartbeat", false)]
    [InlineData("heartbeat", true)]
    [InlineData("status", false)]
    [InlineData("vote", false)]
    public async Task CleanupCASPreservesConcurrentUpdates(string update, bool legacy)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var otherWriter = store.CreateTable();
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        store.Seed(entry, heartbeat: !legacy);
        var first = await table.ReadAllAsync(CancellationToken);
        var original = Assert.Single(first.Members);
        store.BeforeTransaction = async () =>
        {
            if (update == "heartbeat")
            {
                entry.IAmAliveTime = Epoch.AddDays(2);
                await otherWriter.UpdateIAmAliveAsync(entry, CancellationToken);
            }
            else
            {
                if (update == "status")
                {
                    entry.Status = SiloStatus.Active;
                }
                else
                {
                    entry.SuspectTimes = new() { Tuple.Create(Entry(2).SiloAddress, Epoch.AddDays(2)) };
                }

                Assert.True(await otherWriter.UpdateRowAsync(entry, original.Item2, first.Version.Next(), CancellationToken));
            }
        };
        await table.CleanupDefunctSiloEntriesAsync(Epoch.AddDays(1), CancellationToken);
        var result = await table.ReadAllAsync(CancellationToken);
        var survivor = Assert.Single(result.Members);
        Assert.Equal(entry.Status, survivor.Item1.Status);
        Assert.Equal(entry.IAmAliveTime, survivor.Item1.IAmAliveTime);
        Assert.Equal(entry.SuspectTimes, survivor.Item1.SuspectTimes);
        Assert.Equal(update == "heartbeat" ? first.Version.Version : first.Version.Version + 1, result.Version.Version);
        Assert.Equal(1, store.ConflictCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredHeartbeatCannotRecreateRowHeartbeatOrVersion(bool legacy)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var otherWriter = store.CreateTable();
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        store.Seed(entry, heartbeat: !legacy);
        if (!legacy)
        {
            await table.InitializeMembershipTableAsync(true, CancellationToken);
        }

        var first = await table.ReadAllAsync(CancellationToken);
        store.BeforeTransaction = () => otherWriter.CleanupDefunctSiloEntriesAsync(Epoch.AddDays(1), CancellationToken);
        entry.IAmAliveTime = Epoch.AddDays(2);
        await table.UpdateIAmAliveAsync(entry, CancellationToken);
        await table.UpdateIAmAliveAsync(entry, CancellationToken);
        Assert.False(await table.UpdateRowAsync(entry, Assert.Single(first.Members).Item2, first.Version.Next(), CancellationToken));
        var result = await table.ReadAllAsync(CancellationToken);
        Assert.Empty(result.Members);
        Assert.Equal(first.Version, result.Version);
        Assert.Equal(legacy ? Array.Empty<string>() : new[] { "orleans/cluster/version" }, store.Keys);
        Assert.Equal(1, store.ConflictCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("membership/nested")]
    public async Task CleanupAndDeleteRespectClusterPrefixAndRequestedCluster(string? root)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable("cluster", root);
        var neighbor = store.CreateTable("cluster-other", root);
        var dead = Entry();
        dead.Status = SiloStatus.Dead;
        Assert.True(await table.InsertRowAsync(dead, new TableVersion(1, "0"), CancellationToken));
        Assert.True(await neighbor.InsertRowAsync(dead, new TableVersion(1, "0"), CancellationToken));
        await table.CleanupDefunctSiloEntriesAsync(Epoch.AddDays(1), CancellationToken);
        Assert.Empty((await table.ReadAllAsync(CancellationToken)).Members);
        Assert.Single((await neighbor.ReadAllAsync(CancellationToken)).Members);

        Assert.True(await table.InsertRowAsync(Entry(2), (await table.ReadAllAsync(CancellationToken)).Version.Next(), CancellationToken));
        var before = await table.ReadAllAsync(CancellationToken);
        await table.DeleteMembershipTableEntriesAsync("cluster-other", CancellationToken);
        Assert.Empty((await neighbor.ReadAllAsync(CancellationToken)).Members);
        var after = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(Entry(2).SiloAddress, Assert.Single(after.Members).Item1.SiloAddress);
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("read-all")]
    [InlineData("read-row")]
    [InlineData("insert")]
    [InlineData("update")]
    [InlineData("heartbeat")]
    [InlineData("cleanup")]
    [InlineData("delete")]
    public async Task BackendFailuresPropagate(string operation)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), CancellationToken));
        var first = await table.ReadAllAsync(CancellationToken);
        var before = store.Snapshot();
        entry.IAmAliveTime = Epoch.AddHours(2);
        store.FailureMethod = operation is "read-all" or "read-row" ? HttpMethod.Get
            : operation == "delete" ? HttpMethod.Delete : HttpMethod.Put;
        var exception = await Assert.ThrowsAsync<ConsulRequestException>(() => operation switch
        {
            "initialize" => table.InitializeMembershipTableAsync(true, CancellationToken),
            "read-all" => table.ReadAllAsync(CancellationToken),
            "read-row" => table.ReadRowAsync(entry.SiloAddress, CancellationToken),
            "insert" => table.InsertRowAsync(Entry(2), first.Version.Next(), CancellationToken),
            "update" => table.UpdateRowAsync(entry, Assert.Single(first.Members).Item2, first.Version.Next(), CancellationToken),
            "heartbeat" => table.UpdateIAmAliveAsync(entry, CancellationToken),
            "cleanup" => table.CleanupDefunctSiloEntriesAsync(Epoch.AddDays(1), CancellationToken),
            "delete" => table.DeleteMembershipTableEntriesAsync("cluster", CancellationToken),
            _ => throw new InvalidOperationException(operation)
        });
        Assert.Contains("injected failure", exception.Message);
        Assert.Equal(before, store.Snapshot());
    }

    [Fact]
    public async Task DeleteFailureResponseAndMalformedVersionAreSurfaced()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        store.DeleteResult = false;
        await Assert.ThrowsAsync<OrleansException>(() => table.DeleteMembershipTableEntriesAsync("cluster", CancellationToken));
        store.Set("orleans/cluster/version", Encoding.UTF8.GetBytes("invalid"));
        await Assert.ThrowsAsync<FormatException>(() => table.ReadAllAsync(CancellationToken));
        await Assert.ThrowsAsync<FormatException>(() => table.ReadRowAsync(Entry().SiloAddress, CancellationToken));
        await Assert.ThrowsAsync<FormatException>(() => table.InsertRowAsync(Entry(), new TableVersion(1, "invalid"), CancellationToken));
    }

    [Fact]
    public async Task CancellationStopsHeartbeatConflictRetries()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        store.Seed(entry);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        store.BeforeTransaction = () =>
        {
            store.Seed(entry);
            cancellation.Cancel();
            return Task.CompletedTask;
        };
        entry.IAmAliveTime = Epoch.AddHours(2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => table.UpdateIAmAliveAsync(entry, cancellation.Token));
        Assert.Single(store.Transactions);
    }

    private static MembershipEntry Entry(int id = 1) => new()
    {
        SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 10000 + id), 1),
        HostName = "host",
        SiloName = $"silo-{id}",
        Status = SiloStatus.Active,
        StartTime = Epoch,
        IAmAliveTime = Epoch.AddHours(1),
        SuspectTimes = new()
    };

    // Exercises the real SDK's serialization, HTTP status handling, and transaction results.
    // Hooks interleave independent providers immediately before the backend applies a transaction.
    private sealed class ConsulHandler : HttpMessageHandler
    {
        private Dictionary<string, KVPair> _rows = new(StringComparer.Ordinal);
        private readonly List<HttpClient> _httpClients = new();
        private readonly List<ConsulClient> _clients = new();
        private ulong _index;

        public ConsulClient Client => _clients[0];
        public IEnumerable<string> Keys => _rows.Keys;
        public int RequestCount { get; private set; }
        public int ConflictCount { get; private set; }
        public List<List<KVTxnOp>> Transactions { get; } = new();
        public Func<Task>? BeforeTransaction { get; set; }
        public HttpMethod? FailureMethod { get; set; }
        public bool DeleteResult { get; set; } = true;

        public ConsulBasedMembershipTable CreateTable(string cluster = "cluster", string? root = null)
        {
            var http = new HttpClient(this, disposeHandler: false);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            var client = new ConsulClient(new ConsulClientConfiguration { Address = new Uri("http://consul.invalid") }, http);
            _httpClients.Add(http);
            _clients.Add(client);
            var options = new ConsulClusteringOptions { KvRootFolder = root };
            options.ConfigureConsulClient(() => client);
            return new(NullLogger<ConsulBasedMembershipTable>.Instance, Options.Create(options),
                Options.Create(new ClusterOptions { ClusterId = cluster }));
        }

        public void Seed(MembershipEntry entry, bool heartbeat = true)
        {
            var key = $"orleans/cluster/{entry.SiloAddress.ToParsableString()}";
            Set(key, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
            {
                Hostname = entry.HostName,
                entry.ProxyPort,
                entry.StartTime,
                entry.Status,
                entry.SiloName,
                SuspectingSilos = entry.SuspectTimes?.Select(vote => new { Id = vote.Item1.ToParsableString(), Time = vote.Item2 })
            })));
            if (heartbeat)
            {
                Set(key + "/iamalive", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(entry.IAmAliveTime)));
            }
        }

        public void Set(string key, byte[] value) => _rows[key] = new(key) { Value = value, ModifyIndex = ++_index };

        public string Snapshot() => JsonConvert.SerializeObject(_rows.Values.OrderBy(row => row.Key, StringComparer.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(++RequestCount <= 200, "Consul request budget exhausted: possible unbounded retry.");
            if (request.Method == FailureMethod)
            {
                return Response(HttpStatusCode.InternalServerError, "injected failure");
            }

            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/v1/txn" && request.Method == HttpMethod.Put)
            {
                var body = JArray.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var operations = body.Select(item => item["KV"]!.ToObject<KVTxnOp>()!).ToList();
                Transactions.Add(operations);
                if (BeforeTransaction is { } before)
                {
                    BeforeTransaction = null;
                    await before();
                }

                var next = new Dictionary<string, KVPair>(_rows, StringComparer.Ordinal);
                var index = ++_index;
                for (var i = 0; i < operations.Count; i++)
                {
                    var operation = operations[i];
                    next.TryGetValue(operation.Key, out var current);
                    var matches = operation.Verb.Equals(KVTxnVerb.DeleteCAS)
                        ? current is null || current.ModifyIndex == operation.Index
                        : operation.Verb.Equals(KVTxnVerb.CheckIndex)
                            ? current is not null && current.ModifyIndex == operation.Index
                            : (current?.ModifyIndex ?? 0) == operation.Index;
                    if (!matches)
                    {
                        ConflictCount++;
                        return Response(HttpStatusCode.Conflict, JsonConvert.SerializeObject(new
                        {
                            Errors = new[] { new { OpIndex = i, What = "CAS index mismatch" } }
                        }));
                    }

                    if (operation.Verb.Equals(KVTxnVerb.CAS))
                    {
                        next[operation.Key] = new(operation.Key) { Value = operation.Value, ModifyIndex = index };
                    }
                    else if (operation.Verb.Equals(KVTxnVerb.DeleteCAS))
                    {
                        next.Remove(operation.Key);
                    }
                    else
                    {
                        Assert.Equal(KVTxnVerb.CheckIndex, operation.Verb);
                    }
                }

                _rows = next;
                return Response(HttpStatusCode.OK, "{\"Results\":[]}");
            }

            Assert.StartsWith("/v1/kv/", uri.AbsolutePath);
            var key = Uri.UnescapeDataString(uri.AbsolutePath["/v1/kv/".Length..]);
            Assert.Contains("recurse", uri.Query);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Contains("consistent", uri.Query);
                var matches = _rows.Values.Where(row => row.Key.StartsWith(key, StringComparison.Ordinal)).ToArray();
                return matches.Length == 0
                    ? Response(HttpStatusCode.NotFound, string.Empty)
                    : Response(HttpStatusCode.OK, JsonConvert.SerializeObject(matches));
            }

            Assert.Equal(HttpMethod.Delete, request.Method);
            if (DeleteResult)
            {
                foreach (var candidate in _rows.Keys.Where(candidate => candidate.StartsWith(key, StringComparison.Ordinal)).ToArray())
                {
                    _rows.Remove(candidate);
                }
            }

            return Response(HttpStatusCode.OK, DeleteResult ? "true" : "false");
        }

        private HttpResponseMessage Response(HttpStatusCode status, string body)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.Add("X-Consul-Index", _index.ToString(CultureInfo.InvariantCulture));
            response.Headers.Add("X-Consul-Knownleader", "true");
            response.Headers.Add("X-Consul-Lastcontact", "0");
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var client in _clients)
                {
                    client.Dispose();
                }

                foreach (var http in _httpClients)
                {
                    http.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
