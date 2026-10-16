using System.Text.Json;

namespace Orleans.Journaling.Garnet.Tests.Conformance;

/// <summary>
/// Reads Quint's Informal Trace Format (ITF), as written by <c>quint run --mbt --out-itf</c>.
/// </summary>
internal static class Itf
{
    public static long Int(JsonElement value) => long.Parse(value.GetProperty("#bigint").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    public static IEnumerable<(JsonElement Key, JsonElement Value)> Entries(JsonElement map)
        => map.GetProperty("#map").EnumerateArray().Select(pair => (pair[0], pair[1]));

    public static IEnumerable<JsonElement> Set(JsonElement set) => set.GetProperty("#set").EnumerateArray();

    /// <summary>Gets a state variable by its unqualified name.</summary>
    public static JsonElement Var(JsonElement state, string name)
        => state.EnumerateObject().First(property => property.Name.EndsWith("::" + name, StringComparison.Ordinal)).Value;

    public static string Action(JsonElement state) => state.GetProperty("mbt::actionTaken").GetString()!;

    /// <summary>Gets the value of a nondeterministic choice the step made, if it made it.</summary>
    public static JsonElement? Pick(JsonElement state, string name)
        => state.GetProperty("mbt::nondetPicks").TryGetProperty(name, out var pick) && pick.GetProperty("tag").GetString() == "Some"
            ? pick.GetProperty("value")
            : null;

    public static long IntPick(JsonElement state, string name) => Int(Pick(state, name) ?? throw new InvalidOperationException($"The step did not choose '{name}'."));

    public static bool BoolPick(JsonElement state, string name) => (Pick(state, name) ?? throw new InvalidOperationException($"The step did not choose '{name}'.")).GetBoolean();

    public static string StringPick(JsonElement state, string name) => (Pick(state, name) ?? throw new InvalidOperationException($"The step did not choose '{name}'.")).GetString()!;
}

/// <summary>A bucket's state in a partition, in the specification.</summary>
internal sealed record SpecBucket(string State, long Version, long SealVersion, long Source, bool Done, bool Reclaimed, long Generation);

/// <summary>
/// A journal record in a partition, in the specification. <paramref name="Identity"/> is one more than the id of the
/// op which wrote the record, or 0 for no record and absent markers. <paramref name="DeletionGeneration"/> is a
/// tombstone's stamp: the bucket's collection generation when its deletion committed.
/// </summary>
internal sealed record SpecRecord(string Kind, IReadOnlyList<long> Values, long Identity, IReadOnlySet<long> Ids, long DeletionGeneration);

/// <summary>
/// An operation issued by a client, in the specification: conditioned on a live journal or an absence, named by the
/// identity the client observed (-1 for a stale condition, which no state has), with the collection generation
/// <paramref name="Observed"/> observed with it.
/// </summary>
internal sealed record SpecOp(string Key, bool Delete, bool Live, long Expect, long Observed);

/// <summary>An authorized open of a bucket, in the specification.</summary>
internal sealed record SpecTicket(long Partition, int Bucket, long View, long Source);

/// <summary>One state of a specification trace.</summary>
internal sealed class SpecState
{
    public SpecState(JsonElement state)
    {
        Raw = state;
        Action = Itf.Action(state);
        Views = Itf.Var(state, "views").EnumerateArray()
            .Select(view => Itf.Entries(view).ToDictionary(entry => Itf.Int(entry.Key), entry => Itf.Int(entry.Value)))
            .ToList();
        foreach (var (partition, buckets) in Itf.Entries(Itf.Var(state, "bkt")))
        {
            foreach (var (bucket, value) in Itf.Entries(buckets))
            {
                Buckets[(Itf.Int(partition), Itf.Int(bucket))] = new SpecBucket(
                    value.GetProperty("st").GetString()!,
                    Itf.Int(value.GetProperty("ver")),
                    Itf.Int(value.GetProperty("sealVer")),
                    Itf.Int(value.GetProperty("src")),
                    value.GetProperty("done").GetBoolean(),
                    value.GetProperty("reclaimed").GetBoolean(),
                    Itf.Int(value.GetProperty("gen")));
            }
        }

        foreach (var (partition, records) in Itf.Entries(Itf.Var(state, "jr")))
        {
            foreach (var (key, value) in Itf.Entries(records))
            {
                Records[(Itf.Int(partition), key.GetString()!)] = new SpecRecord(
                    value.GetProperty("kind").GetString()!,
                    value.GetProperty("vals").EnumerateArray().Select(Itf.Int).ToList(),
                    Itf.Int(value.GetProperty("ver")),
                    value.GetProperty("ids").EnumerateArray().Select(Itf.Int).ToHashSet(),
                    Itf.Int(value.GetProperty("dgen")));
            }
        }

        foreach (var (id, value) in Itf.Entries(Itf.Var(state, "ops")))
        {
            Ops[Itf.Int(id)] = new SpecOp(
                value.GetProperty("key").GetString()!,
                value.GetProperty("del").GetBoolean(),
                value.GetProperty("live").GetBoolean(),
                Itf.Int(value.GetProperty("expect")),
                Itf.Int(value.GetProperty("obs")));
        }

        foreach (var (id, value) in Itf.Entries(Itf.Var(state, "outcome")))
        {
            Outcomes[Itf.Int(id)] = value.GetString()!;
        }

        foreach (var (slot, value) in Itf.Entries(Itf.Var(state, "opens")))
        {
            if (value.GetProperty("active").GetBoolean())
            {
                Opens[Itf.Int(slot)] = new SpecTicket(
                    Itf.Int(value.GetProperty("m")),
                    (int)Itf.Int(value.GetProperty("b")),
                    Itf.Int(value.GetProperty("v")),
                    Itf.Int(value.GetProperty("src")));
            }
        }

        foreach (var (slot, value) in Itf.Entries(Itf.Var(state, "reclaims")))
        {
            if (value.GetProperty("active").GetBoolean())
            {
                Reclaims.Add(Itf.Int(slot));
            }
        }

        NextOp = Itf.Int(Itf.Var(state, "nextOp"));
    }

    public JsonElement Raw { get; }

    public string Action { get; }

    public List<Dictionary<long, long>> Views { get; }

    public Dictionary<(long Partition, long Bucket), SpecBucket> Buckets { get; } = [];

    public Dictionary<(long Partition, string Key), SpecRecord> Records { get; } = [];

    public Dictionary<long, SpecOp> Ops { get; } = [];

    public Dictionary<long, string> Outcomes { get; } = [];

    public Dictionary<long, SpecTicket> Opens { get; } = [];

    /// <summary>Gets the reclamation request slots which are active.</summary>
    public HashSet<long> Reclaims { get; } = [];

    public long NextOp { get; }

    public static List<SpecState> Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("states").EnumerateArray().Select(state => new SpecState(state.Clone())).ToList();
    }
}
