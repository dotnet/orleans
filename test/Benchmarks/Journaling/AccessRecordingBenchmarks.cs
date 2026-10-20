using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Benchmarks.Journaling;

/// <summary>
/// Measures what recording a durable task body's accesses to journaled state costs, per access and per body: outside a
/// body (the baseline of each group), in a recording body (including staging and journaling the records, and removing the
/// log as completion does), and in a replaying body.
/// </summary>
/// <remarks>
/// <para>
/// Reads are <c>TryGetValue</c> of a dictionary of 64 entries, or <c>Value</c> of a <c>DurableValue</c>, whose values are
/// 32-character strings or instances of a record class (<see cref="ValueKind"/>). Bodies run as attempts of a task with a
/// hierarchical identifier, through the runtime's context provider; see <see cref="AccessRecordingFixture"/>.
/// </para>
/// <para>
/// Set <c>BENCH_FORMATS</c> to <c>binary</c> or <c>json</c> to run one journal format. Only the binary format records
/// positions, so with <c>json</c> both values of <see cref="Positions"/> record values.
/// </para>
/// </remarks>
[BenchmarkCategory("Journaling")]
[MemoryDiagnoser(displayGenColumns: false)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AccessRecordingBenchmarks
{
    public const int Ops = 1_024;
    public const int Bodies = 64;
    public const int KeysReads = 64;

    private AccessRecordingFixture _f = null!;

    [ParamsSource(nameof(Formats))]
    public string Format { get; set; } = "binary";

    [ParamsAllValues]
    public AccessRecordingValueKind ValueKind { get; set; }

    /// <summary>Gets or sets whether pure reads record journal positions (P3) rather than values (P2).</summary>
    [ParamsAllValues]
    public bool Positions { get; set; }

    public static IEnumerable<string> Formats => AccessRecordingFixture.Formats;

    [GlobalSetup]
    public void Setup() => _f = AccessRecordingFixture.Create(Format, ValueKind, Positions);

    [GlobalCleanup]
    public void Cleanup() => _f.Dispose();

    // Reads of different keys. Per access.

    [Benchmark(Baseline = true, OperationsPerInvoke = Ops), BenchmarkCategory("Read")]
    public int Read_Outside() => _f.RunOutside(AccessRecordingScenario.ReadDistinct);

    /// <summary>In an attempt without an access log: the provider's dispatch alone.</summary>
    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("Read")]
    public int Read_Unrecorded() => _f.RunUnrecorded(AccessRecordingScenario.ReadDistinct);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("Read")]
    public int Read_Recorded() => _f.Record(AccessRecordingScenario.ReadDistinct);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("Read")]
    public int Read_Replayed() => _f.Replay(AccessRecordingScenario.ReadDistinct);

    // Reads of one key of an unchanged dictionary. Per access.

    [Benchmark(Baseline = true, OperationsPerInvoke = Ops), BenchmarkCategory("ReadSameKey")]
    public int ReadSameKey_Outside() => _f.RunOutside(AccessRecordingScenario.ReadSameKey);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("ReadSameKey")]
    public int ReadSameKey_Recorded() => _f.Record(AccessRecordingScenario.ReadSameKey);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("ReadSameKey")]
    public int ReadSameKey_Replayed() => _f.Replay(AccessRecordingScenario.ReadSameKey);

    // Reads of an unchanged dictionary which alternate between two keys. Per access.

    [Benchmark(Baseline = true, OperationsPerInvoke = Ops), BenchmarkCategory("ReadAlternating")]
    public int ReadAlternating_Outside() => _f.RunOutside(AccessRecordingScenario.ReadAlternating);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("ReadAlternating")]
    public int ReadAlternating_Recorded() => _f.Record(AccessRecordingScenario.ReadAlternating);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("ReadAlternating")]
    public int ReadAlternating_Replayed() => _f.Replay(AccessRecordingScenario.ReadAlternating);

    // Reads of an unchanged value. Per access.

    [Benchmark(Baseline = true, OperationsPerInvoke = Ops), BenchmarkCategory("ReadValue")]
    public int ReadValue_Outside() => _f.RunOutside(AccessRecordingScenario.ReadValue);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("ReadValue")]
    public int ReadValue_Recorded() => _f.Record(AccessRecordingScenario.ReadValue);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("ReadValue")]
    public int ReadValue_Replayed() => _f.Replay(AccessRecordingScenario.ReadValue);

    // Per iteration: a read of one key and a write of another key of the same dictionary.

    [Benchmark(Baseline = true, OperationsPerInvoke = Ops), BenchmarkCategory("ReadAWriteB")]
    public int ReadAWriteB_Outside() => _f.RunOutside(AccessRecordingScenario.ReadAWriteB);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("ReadAWriteB")]
    public int ReadAWriteB_Recorded() => _f.Record(AccessRecordingScenario.ReadAWriteB);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("ReadAWriteB")]
    public int ReadAWriteB_Replayed() => _f.Replay(AccessRecordingScenario.ReadAWriteB);

    // Writes of a value. Per access.

    [Benchmark(Baseline = true, OperationsPerInvoke = Ops), BenchmarkCategory("WriteValue")]
    public int WriteValue_Outside() => _f.RunOutside(AccessRecordingScenario.WriteValue);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("WriteValue")]
    public int WriteValue_Recorded() => _f.Record(AccessRecordingScenario.WriteValue);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("WriteValue")]
    public int WriteValue_Replayed() => _f.Replay(AccessRecordingScenario.WriteValue);

    // Sets of dictionary entries. Per access.

    [Benchmark(Baseline = true, OperationsPerInvoke = Ops), BenchmarkCategory("WriteDictionary")]
    public int WriteDictionary_Outside() => _f.RunOutside(AccessRecordingScenario.WriteDictionary);

    [Benchmark(OperationsPerInvoke = Ops), BenchmarkCategory("WriteDictionary")]
    public int WriteDictionary_Recorded() => _f.Record(AccessRecordingScenario.WriteDictionary);

    // Per read of Keys, 64 keys, of an unchanged dictionary.

    [Benchmark(Baseline = true, OperationsPerInvoke = KeysReads), BenchmarkCategory("Keys")]
    public int Keys_Outside() => _f.RunOutside(AccessRecordingScenario.Keys);

    [Benchmark(OperationsPerInvoke = KeysReads), BenchmarkCategory("Keys")]
    public int Keys_Recorded() => _f.Record(AccessRecordingScenario.Keys);

    // Per body: a typical step (3 reads, 3 writes, 1 count). Each attempt but the replayed ones journals and completes.

    [Benchmark(Baseline = true, OperationsPerInvoke = Bodies), BenchmarkCategory("TypicalBody")]
    public int TypicalBody_Outside() => _f.RunOutside(AccessRecordingScenario.TypicalBody, Bodies);

    /// <summary>In attempts without an access log: the attempt and the provider's dispatch, without recording.</summary>
    [Benchmark(OperationsPerInvoke = Bodies), BenchmarkCategory("TypicalBody")]
    public int TypicalBody_Unrecorded() => _f.RunUnrecorded(AccessRecordingScenario.TypicalBody, Bodies);

    [Benchmark(OperationsPerInvoke = Bodies), BenchmarkCategory("TypicalBody")]
    public int TypicalBody_Recorded() => _f.Record(AccessRecordingScenario.TypicalBody, Bodies);

    [Benchmark(OperationsPerInvoke = Bodies), BenchmarkCategory("TypicalBody")]
    public int TypicalBody_Replayed() => _f.Replay(AccessRecordingScenario.TypicalBody, Bodies);
}
