using System.Globalization;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests.Conformance;

/// <summary>
/// Replays specification traces (<c>specs/garnet-range-migration</c>) against the real journal procedures, migrator,
/// coordinator, and an in-memory MetaStore, and compares the observable state after every step.
/// </summary>
/// <remarks>
/// <para>
/// Specification partitions 1, 2, and 3 are partitions <c>p0</c>, <c>p1</c>, and <c>p2</c>; buckets 0 and 1 are the
/// map's two buckets; keys <c>a</c>, <c>b</c>, and <c>c</c> are journals in buckets 0, 0, and 1. View <c>v</c> is the map
/// version which committed it (version 0 for the initial view). Op <c>id</c> writes ETag <c>op-{id}</c> and appends
/// the single byte <c>id</c>.
/// </para>
/// <para>
/// Each step runs the procedure the action names, at the partition it names, with the arguments it chose, and checks
/// that the implementation accepts it where the specification did and reports the same outcome. After every step the
/// harness compares, for every partition, each bucket's state, version, source, <c>done</c>, and reclamation, and each
/// journal's record kind, content, and recent mutation ETags; and the owner of each bucket in the MetaStore's map.
/// </para>
/// </remarks>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetJournalConformanceTests
{
    private static readonly string[] Keys = ["a", "b", "c"];
    private static readonly long[] Buckets = [0, 1];
    private static readonly long[] Partitions = [1, 2, 3];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public static TheoryData<string> Traces
    {
        get
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Conformance", "traces");
            return [.. Directory.GetFiles(directory, "*.itf.json").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];
        }
    }

    [Theory]
    [MemberData(nameof(Traces))]
    public async Task Implementation_ConformsToSpecificationTrace(string trace)
    {
        var states = SpecState.Load(Path.Combine(AppContext.BaseDirectory, "Conformance", "traces", trace));
        await using var partitions = await GarnetTestPartitions.StartAsync(3, 2, owners: 2, CancellationToken);
        var replayer = new Replayer(partitions);
        await replayer.CompareAsync(states[0], 0);
        for (var i = 1; i < states.Count; i++)
        {
            try
            {
                await replayer.StepAsync(states[i - 1], states[i]);
                await replayer.CompareAsync(states[i], i);
            }
            catch (Exception exception)
            {
                throw new ConformanceException($"Step {i} ({states[i].Action}) of {trace} failed: {exception.Message}", exception);
            }
        }
    }

    [Fact]
    public void FilterCheck_RejectsAFilterOmittingALiveJournalOrTombstone()
    {
        byte[] live = [.. "live-journal"u8], tombstone = [.. "deleted-journal"u8];
        var journals = new[] { ("a", live), ("b", tombstone) };
        HashSet<string> required = ["a", "b"];
        Replayer.CheckFilter("test", "filter", GarnetJournalKeyFilter.Create([live, tombstone]), required, required, journals);

        // A present filter which reports nothing, or which omits either journal, fails the check.
        Assert.Throws<ConformanceException>(() => Replayer.CheckFilter("test", "filter", GarnetJournalKeyFilter.Create([]), required, required, journals));
        Assert.Throws<ConformanceException>(() => Replayer.CheckFilter("test", "filter", GarnetJournalKeyFilter.Create([live]), required, required, journals));
        Assert.Throws<ConformanceException>(() => Replayer.CheckFilter("test", "filter", GarnetJournalKeyFilter.Create([tombstone]), required, required, journals));
    }

    private sealed class ConformanceException(string message, Exception? inner = null) : Exception(message, inner);

    private sealed class Replayer
    {
        private readonly GarnetTestPartitions _partitions;
        private readonly GarnetJournalRouter _router;
        private readonly GarnetJournalMigrationCoordinator _coordinator;
        private readonly Dictionary<string, GarnetJournalKeySet> _keys = [];
        private readonly Dictionary<long, (string Procedure, object[] Args, SpecOp Op)> _ops = [];
        private readonly List<long> _viewVersions = [0];
        private readonly Dictionary<long, (long Partition, string Key, string Version, ChainRecord Record)> _pulls = [];
        private readonly Dictionary<long, (long Partition, int Bucket, long View, long Source, long Generation, byte[]? Filter)> _opens = [];
        private readonly Dictionary<long, (long Partition, int Bucket, long Version, List<GarnetJournalKeySet> Journals)> _dones = [];
        private readonly Dictionary<long, (long Partition, int Bucket, long Version)> _reclaims = [];

        // The journals each key filter must report (D36): the records its copy held when sealed, and its chain's required
        // journals while its migration was not done. The specification's filters may also report false positives, so
        // these are tracked separately.
        private readonly Dictionary<(long Partition, long Bucket), HashSet<string>> _requiredFilters = [];
        private readonly Dictionary<(long Partition, long Bucket), HashSet<string>> _requiredChains = [];
        private readonly Dictionary<long, HashSet<string>> _requiredOpens = [];

        public Replayer(GarnetTestPartitions partitions)
        {
            _partitions = partitions;
            _router = partitions.CreateRouter(TimeSpan.FromSeconds(5));
            _coordinator = new GarnetJournalMigrationCoordinator(_router);
            var buckets = new Dictionary<string, int> { ["a"] = 0, ["b"] = 0, ["c"] = 1 };
            foreach (var key in Keys)
            {
                var id = partitions.JournalsInBucket(buckets[key], $"conformance-{key}").First();
                _keys[key] = GarnetJournalKeySet.Create(partitions.MapId, partitions.BucketCount, GarnetJournalKeys.EncodeJournalId(id.Value));
            }
        }

        private GarnetJournalMigrator Migrator => _router.Migrator;

        private static PartitionId P(long partition) => GarnetTestPartitions.Id((int)partition - 1);

        public async Task StepAsync(SpecState pre, SpecState post)
        {
            var state = post.Raw;
            switch (post.Action)
            {
                case "writeWith":
                    await WriteAsync(pre, post);
                    break;
                case "deliverAttempt":
                    {
                        // A delayed duplicate of an attempt executes after the client has its outcome; it changes no
                        // outcome, so only the partition's state is compared.
                        var (procedure, args, _) = _ops[Itf.IntPick(state, "cid")];
                        var status = (await RunAsync(P(Itf.IntPick(state, "cp")), procedure, args))[0].ToString();
                        Expect(status is "1" or "2" or "-1", $"a delayed attempt returned status {status}");
                        break;
                    }

                case "expire":
                    // An attempt the client no longer sends; nothing runs in the partitions.
                    break;
                case "retry":
                    {
                        var id = Itf.IntPick(state, "cid");
                        var (procedure, args, op) = _ops[id];
                        var result = await RunAsync(P(Itf.IntPick(state, "cp")), procedure, args);
                        CheckOutcome(post, id, op, result, Itf.BoolPick(state, "clost"));
                        break;
                    }

                case "changeViewWith":
                    {
                        // Phases are recorded from observed partition state before the coordinator applies its rule (D11).
                        await _coordinator.RecordObservedAsync([0, 1], CancellationToken);
                        var buckets = Itf.Set(Itf.Pick(state, "vbs")!.Value).Select(value => (int)Itf.Int(value)).ToList();
                        var move = await _coordinator.TryMoveAsync(buckets, P(Itf.IntPick(state, "vp")), CancellationToken);
                        Expect(move.Status == MoveStatus.Committed, $"the coordinator refused a move the specification's coordinator rule allows: {move.Status}");
                        _viewVersions.Add(move.View.Version);
                        break;
                    }

                case "sealAny":
                    {
                        var (partition, bucket) = (Itf.IntPick(state, "sp"), Itf.IntPick(state, "sb"));
                        var version = _viewVersions[(int)post.Buckets[(partition, bucket)].SealVersion];

                        // The specification chose whether the seal writes key filters (D36), and any false positives they
                        // report; the implementation's filters report none in buckets this small.
                        var filters = Itf.Pick(state, "sf")?.GetBoolean() ?? false;
                        Expect(await Migrator.TransitionAsync(P(partition), (int)bucket, GarnetJournalProtocol.BucketSealOperation, version, null, CancellationToken, extra: filters ? "1" : "0"), "seal was refused");
                        var chain = pre.Buckets[(partition, bucket)].Done ? [] : _requiredChains.GetValueOrDefault((partition, bucket)) ?? [];
                        _requiredFilters[(partition, bucket)] = [.. Keys.Where(key => _keys[key].Bucket == bucket && pre.Records[(partition, key)].Kind != "none"), .. chain];
                        break;
                    }

                case "authorizeAny":
                    {
                        // The coordinator observes the sealed source and reads its collection generation and key filters,
                        // which are final.
                        var slot = Itf.IntPick(state, "ai");
                        var ticket = post.Opens[slot];
                        var source = await Migrator.GetBucketAsync(P(ticket.Source), ticket.Bucket, CancellationToken, withFilter: true);
                        _opens[slot] = (ticket.Partition, ticket.Bucket, ticket.View, ticket.Source, source.CollectionGeneration, source.State == "sealed" ? source.Filter : null);
                        _requiredOpens[slot] = source.State == "sealed" ? _requiredFilters.GetValueOrDefault((ticket.Source, ticket.Bucket)) ?? [] : [];
                        break;
                    }

                case "openAny":
                    {
                        var slot = Itf.IntPick(state, "oi");
                        var ticket = _opens[slot];
                        _opens.Remove(slot);
                        _requiredOpens.Remove(slot, out var required);
                        Expect(
                            await Migrator.TransitionAsync(P(ticket.Partition), ticket.Bucket, GarnetJournalProtocol.BucketOpenOperation, _viewVersions[(int)ticket.View], P(ticket.Source), CancellationToken, ticket.Generation, ticket.Filter),
                            "open was refused");
                        _requiredChains[(ticket.Partition, ticket.Bucket)] = required!;
                        break;
                    }

                case "dropTicketAny":
                    {
                        // The partition rejects an open its version fences out; one already applied at the same version
                        // reports success without changing anything.
                        var slot = Itf.IntPick(state, "ti");
                        var ticket = _opens[slot];
                        _opens.Remove(slot);
                        _requiredOpens.Remove(slot);
                        var applied = await Migrator.TransitionAsync(P(ticket.Partition), ticket.Bucket, GarnetJournalProtocol.BucketOpenOperation, _viewVersions[(int)ticket.View], P(ticket.Source), CancellationToken, ticket.Generation, ticket.Filter);
                        var before = pre.Buckets[(ticket.Partition, ticket.Bucket)];
                        Expect(!applied || before.State == "open" && before.Version == ticket.View, "an open the specification rejects was applied");
                        break;
                    }

                case "fetchAny":
                    {
                        // The fetch reads the chain for the destination's current opening, which fences its install.
                        var (slot, partition, key) = (Itf.IntPick(state, "fi"), Itf.IntPick(state, "fp"), Itf.StringPick(state, "fk"));
                        var (version, record) = await ReadChainAsync(partition, key);
                        _pulls.Add(slot, (partition, key, version, record));
                        break;
                    }

                case "deliverAny":
                    {
                        var slot = Itf.IntPick(state, "li");
                        var pull = _pulls[slot];
                        _pulls.Remove(slot);
                        var installed = await Migrator.TryInstallAsync(_keys[pull.Key], P(pull.Partition), pull.Version, pull.Record, CancellationToken);
                        Expect(installed == true, $"the install returned {installed?.ToString() ?? "WrongPartition"}, but the specification delivered it");
                        break;
                    }

                case "dropPullAny":
                    {
                        var slot = Itf.IntPick(state, "xi");
                        var pull = _pulls[slot];
                        _pulls.Remove(slot);
                        var installed = await Migrator.TryInstallAsync(_keys[pull.Key], P(pull.Partition), pull.Version, pull.Record, CancellationToken);
                        Expect(installed != true, "the install applied, but the specification rejected it");
                        break;
                    }

                case "prepareDoneAny":
                    {
                        // The consolidator lists the inventory from the chain at the destination's current opening (D46).
                        var (slot, partition, bucket) = (Itf.IntPick(state, "di"), Itf.IntPick(state, "dp"), (int)Itf.IntPick(state, "db"));
                        var current = await Migrator.GetBucketAsync(P(partition), bucket, CancellationToken);
                        var members = await Migrator.TryListChainAsync(bucket, new PartitionId(current.Source), CancellationToken)
                            ?? throw new ConformanceException("the migration chain could not be listed");
                        _dones[slot] = (partition, bucket, current.Version, members.Select(member => new GarnetJournalKeySet(_partitions.MapId, bucket, (byte[])member!)).ToList());
                        break;
                    }

                case "completeAny":
                    {
                        var slot = Itf.IntPick(state, "ci");
                        var done = _dones[slot];
                        _dones.Remove(slot);
                        Expect(await TryMarkDoneAsync(done) == true, "completion was refused");
                        _requiredChains.Remove((done.Partition, done.Bucket));
                        break;
                    }

                case "dropDoneAny":
                    {
                        // A completion fenced out by a later opening is rejected; one at the same opening after the
                        // bucket is done reports success without changing anything.
                        var slot = Itf.IntPick(state, "ei");
                        var done = _dones[slot];
                        _dones.Remove(slot);
                        var before = pre.Buckets[(done.Partition, done.Bucket)];
                        var sameOpening = before.State == "open" && _viewVersions[(int)before.Version] == done.Version;
                        Expect(await TryMarkDoneAsync(done) != true || sameOpening && before.Done, "a completion the specification rejects was applied");
                        break;
                    }

                case "authorizeReclaimAny":
                    {
                        // The coordinator binds the reclamation to the version which sealed the copy (D47).
                        var (slot, partition, bucket) = (Itf.IntPick(state, "ri"), Itf.IntPick(state, "rp"), (int)Itf.IntPick(state, "rb"));
                        var current = await Migrator.GetBucketAsync(P(partition), bucket, CancellationToken);
                        Expect(current.State == GarnetJournalProtocol.SealedStateName, "reclamation authorized for a copy which is not sealed");
                        _reclaims[slot] = (partition, bucket, current.Version);
                        break;
                    }

                case "reclaimBatchAny":
                    {
                        // A batch marks the copy reclaimed at its sealing version, then purges the chosen journal if it is
                        // in the bucket; the request retires once the copy is cleared.
                        var (slot, key) = (Itf.IntPick(state, "bi"), Itf.StringPick(state, "bk"));
                        var reclaim = _reclaims[slot];
                        Expect(
                            await Migrator.TransitionAsync(P(reclaim.Partition), reclaim.Bucket, GarnetJournalProtocol.BucketReclaimOperation, reclaim.Version, source: null, CancellationToken),
                            "reclamation was refused");
                        if (_keys[key].Bucket == reclaim.Bucket)
                        {
                            Expect(await Migrator.TryPurgeAsync(P(reclaim.Partition), reclaim.Bucket, reclaim.Version, [(RedisValue)_keys[key].EncodedId], CancellationToken), "the purge batch was refused");
                        }

                        if (!post.Reclaims.Contains(slot))
                        {
                            _reclaims.Remove(slot);
                        }

                        break;
                    }

                case "dropReclaimAny":
                    {
                        // The copy is no longer sealed at the authorized version, so both steps are rejected.
                        var slot = Itf.IntPick(state, "gi");
                        var reclaim = _reclaims[slot];
                        _reclaims.Remove(slot);
                        Expect(
                            !await Migrator.TransitionAsync(P(reclaim.Partition), reclaim.Bucket, GarnetJournalProtocol.BucketReclaimOperation, reclaim.Version, source: null, CancellationToken),
                            "a reclamation the specification rejects was applied");
                        Expect(!await Migrator.TryPurgeAsync(P(reclaim.Partition), reclaim.Bucket, reclaim.Version, [], CancellationToken), "a purge the specification rejects was applied");
                        break;
                    }

                case "dropMarkerAny":
                    {
                        var (partition, key) = (Itf.IntPick(state, "mp"), Itf.StringPick(state, "mk"));
                        // Op ids are the deletion times, and the cutoff follows every deletion.
                        var bucket = _keys[key].Bucket;
                        var args = new object[] { _keys[key].BucketKey, _keys[key].IndexKey, _keys[key].MarkerKey, long.MaxValue.ToString(CultureInfo.InvariantCulture), _keys[key].MetadataKey, (RedisValue)_keys[key].EncodedId };
                        var result = await RunAsync(P(partition), GarnetJournalProtocol.DropMarker, args);
                        Expect(result[0].ToString() == "1" && result[1].ToString() == "1", $"the marker of '{key}' in bucket {bucket} was not dropped");
                        break;
                    }

                default:
                    throw new ConformanceException($"The harness does not replay action '{post.Action}'.");
            }
        }

        private async Task WriteAsync(SpecState pre, SpecState post)
        {
            var state = post.Raw;
            var (partition, key, lost) = (Itf.IntPick(state, "cp"), Itf.StringPick(state, "ck"), Itf.BoolPick(state, "clost"));
            var id = pre.NextOp;
            var op = post.Ops[id];
            Expect(op.Key == key, "the op's key differs from the chosen key");

            // A condition names a state by its identity, one more than the id of the op which wrote it: a journal by
            // its last append's ETag, a tombstone by its deletion's ETag, and no record by the empty ETag (identity 0).
            // The observation may be stale.
            var expected = op.Expect == 0 ? string.Empty : $"op-{op.Expect - 1}";
            var newETag = $"op-{id}";
            var args = new List<object>(GarnetJournalKeySet.Count + 5);
            var keys = new object[GarnetJournalKeySet.Count];
            _keys[key].WriteTo(keys);
            args.AddRange(keys);
            string procedure;
            if (op.Delete)
            {
                procedure = GarnetJournalProtocol.Delete;
                args.AddRange([expected, op.Live ? "1" : "0", newETag, id.ToString(CultureInfo.InvariantCulture)]);
            }
            else
            {
                procedure = GarnetJournalProtocol.Append;
                args.AddRange([op.Live ? "1" : "0", expected, newETag, new byte[] { (byte)id }, "json", op.Observed.ToString(CultureInfo.InvariantCulture)]);
            }

            _ops[id] = (procedure, [.. args], op);
            var result = await RunAsync(P(partition), procedure, [.. args]);
            CheckOutcome(post, id, op, result, lost);
        }

        private static void CheckOutcome(SpecState post, long id, SpecOp op, RedisResult[] result, bool lost)
        {
            var status = result[0].ToString();
            Expect(status is "1" or "2" or "-1", $"op {id} returned status {status}, which the specification does not allow");
            if (lost)
            {
                return;
            }

            // A delete conditioned on absence never commits: the implementation reports it as a no-op or as the
            // journal having appeared, the specification as a conflict. A conflict on a retry is reported as
            // ambiguous (GarnetJournalAmbiguousConflictException).
            var outcome = status == "1" && !(op.Delete && !op.Live) ? "acked" : "conflict";
            var expected = post.Outcomes[id] == "ambiguous" ? "conflict" : post.Outcomes[id];
            Expect(outcome == expected, $"op {id} reported {outcome}, but the specification reports {post.Outcomes[id]}");
        }

        private async Task<(string Version, ChainRecord Record)> ReadChainAsync(long partition, string key)
        {
            var current = await Migrator.GetBucketAsync(P(partition), _keys[key].Bucket, CancellationToken);
            var record = await Migrator.TryReadChainAsync(_keys[key], new PartitionId(current.Source), CancellationToken)
                ?? throw new ConformanceException($"the migration chain of '{key}' could not be read");
            return (current.Version.ToString(CultureInfo.InvariantCulture), record);
        }

        private Task<bool?> TryMarkDoneAsync((long Partition, int Bucket, long Version, List<GarnetJournalKeySet> Journals) done)
            => Migrator.TryMarkDoneAsync(P(done.Partition), done.Bucket, done.Version.ToString(CultureInfo.InvariantCulture), done.Journals, CancellationToken);

        private async Task<RedisResult[]> RunAsync(PartitionId partition, string procedure, object[] args)
            => await _router.ExecuteAtAsync(partition, procedure, args, CancellationToken);

        public async Task CompareAsync(SpecState state, int index)
        {
            var map = (await _partitions.ReadViewAsync()).Map;
            foreach (var bucket in Buckets)
            {
                Expect(P(state.Views[^1][bucket]) == map.GetOwner((int)bucket), $"after step {index}, the map assigns bucket {bucket} to {map.GetOwner((int)bucket)}");
            }

            foreach (var partition in Partitions)
            {
                foreach (var bucket in Buckets)
                {
                    var spec = state.Buckets[(partition, bucket)];
                    var actual = await Migrator.GetBucketAsync(P(partition), (int)bucket, CancellationToken);
                    var where = $"after step {index} ({state.Action}), partition {partition} bucket {bucket}";
                    Expect(actual.State == spec.State, $"{where}: state {actual.State}, expected {spec.State}");
                    if (spec.State == "absent")
                    {
                        continue;
                    }

                    var version = _viewVersions[(int)(spec.State == "sealed" ? spec.SealVersion : spec.Version)];
                    Expect(actual.Version == version, $"{where}: version {actual.Version}, expected {version}");
                    Expect(actual.Source == (spec.Source < 0 ? GarnetJournalProtocol.NoSource : P(spec.Source).Value), $"{where}: source {actual.Source}, expected {spec.Source}");
                    Expect(actual.Done == spec.Done, $"{where}: done {actual.Done}, expected {spec.Done}");
                    Expect(actual.Reclaimed == spec.Reclaimed, $"{where}: reclaimed {actual.Reclaimed}, expected {spec.Reclaimed}");
                    Expect(actual.CollectionGeneration == spec.Generation, $"{where}: collection generation {actual.CollectionGeneration}, expected {spec.Generation}");

                    // Key filters (D36): present where the specification has them, reporting every journal they must,
                    // and no journal the specification's filter (its required journals plus any false positives it
                    // chose) omits.
                    var fields = (await _partitions.Database(P(partition)).HashGetAllAsync(GarnetJournalKeys.GetBucketKey(_partitions.MapId, (int)bucket)))
                        .ToDictionary(entry => entry.Name.ToString(), entry => (byte[])entry.Value!);
                    var journals = Keys.Where(key => _keys[key].Bucket == bucket).Select(key => (key, _keys[key].EncodedId)).ToList();
                    CheckFilter(where, "chain", fields.GetValueOrDefault("chain"), spec.Chain, _requiredChains.GetValueOrDefault((partition, bucket)) ?? [], journals);
                    if (spec.State == "sealed" && !spec.Reclaimed)
                    {
                        CheckFilter(where, "filter", fields.GetValueOrDefault("filter"), spec.Filter, _requiredFilters.GetValueOrDefault((partition, bucket)) ?? [], journals);
                    }
                }

                var database = _partitions.Database(P(partition));
                foreach (var key in Keys)
                {
                    var spec = state.Records[(partition, key)];
                    var metadata = (await database.HashGetAllAsync(_keys[key].MetadataKey)).ToDictionary(entry => entry.Name.ToString(), entry => entry.Value.ToString());
                    var where = $"after step {index} ({state.Action}), partition {partition} journal {key}";
                    var kind = metadata.Count == 0 ? "none"
                        : metadata.ContainsKey("$deleted") ? "tomb"
                        : metadata.ContainsKey("$absent") ? "absent"
                        : "live";
                    Expect(kind == spec.Kind, $"{where}: record {kind}, expected {spec.Kind}");
                    if (kind is "live" or "tomb")
                    {
                        var ids = metadata["$recent-etags"].Split('\n').Select(etag => long.Parse(etag["op-".Length..], CultureInfo.InvariantCulture)).ToHashSet();
                        Expect(ids.SetEquals(spec.Ids), $"{where}: mutations [{string.Join(", ", ids)}], expected [{string.Join(", ", spec.Ids)}]");
                    }

                    if (kind == "tomb")
                    {
                        Expect(metadata["$etag"] == $"op-{spec.Identity - 1}", $"{where}: absence ETag {metadata["$etag"]}, expected op-{spec.Identity - 1}");
                        Expect(metadata["$deleted-gen"] == spec.DeletionGeneration.ToString(CultureInfo.InvariantCulture), $"{where}: deletion generation {metadata["$deleted-gen"]}, expected {spec.DeletionGeneration}");
                    }

                    if (kind == "live")
                    {
                        var values = (await database.ListRangeAsync(_keys[key].DataKey)).Select(value => (long)((byte[])value!)[0]).ToList();
                        Expect(values.SequenceEqual(spec.Values), $"{where}: content [{string.Join(", ", values)}], expected [{string.Join(", ", spec.Values)}]");
                        Expect(metadata["$content-etag"] == $"op-{spec.Values[^1]}", $"{where}: content ETag {metadata["$content-etag"]}");
                    }
                }
            }
        }

        /// <summary>
        /// Checks a key filter against the specification's: present if and only if the specification's is, reporting
        /// every journal in <paramref name="required"/>, and no journal the specification's omits.
        /// </summary>
        public static void CheckFilter(string where, string name, byte[]? actual, IReadOnlySet<string>? expected, IReadOnlySet<string> required, IEnumerable<(string Key, byte[] EncodedId)> journals)
        {
            var present = actual is { Length: > 0 };
            Expect(present == (expected is not null), $"{where}: {name} key filter {(present ? "present" : "absent")}, expected {(expected is null ? "none" : "one")}");
            if (actual is { Length: > 0 } && expected is not null)
            {
                Expect(required.IsSubsetOf(expected), $"{where}: the specification's {name} key filter omits a journal it must report");
                foreach (var (key, id) in journals)
                {
                    var reports = !GarnetJournalKeyFilter.ExcludesAll(actual, id);
                    Expect(!reports || expected.Contains(key), $"{where}: the {name} key filter reports '{key}', which the specification's omits");
                    Expect(reports || !required.Contains(key), $"{where}: the {name} key filter omits '{key}', which it must report");
                }
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition)
            {
                throw new ConformanceException(message);
            }
        }
    }
}
