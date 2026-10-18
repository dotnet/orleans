using BenchmarkDotNet.Attributes;

namespace Benchmarks.Journaling;

/// <summary>
/// Measures what recording a durable task body's accesses to journaled state costs, per access and per body: outside a
/// body, in a recording body (including staging and journaling the records, and removing the log as completion does), and
/// in a replaying body.
/// </summary>
/// <remarks>
/// Reads are <c>TryGetValue</c> of a dictionary of 64 entries with 32-character string values, or <c>Value</c> of a
/// <c>DurableValue&lt;string&gt;</c>. Set <c>BENCH_FORMATS</c> to <c>binary</c> or <c>json</c> to run one journal format.
/// </remarks>
[BenchmarkCategory("Journaling")]
[MemoryDiagnoser(displayGenColumns: false)]
public class AccessRecordingBenchmarks
{
    public const int Ops = 1_024;
    public const int Bodies = 64;

    private AccessRecordingFixture _f = null!;

    [ParamsSource(nameof(Formats))]
    public string Format { get; set; } = "binary";

    public static IEnumerable<string> Formats => AccessRecordingFixture.Formats;

    [GlobalSetup]
    public void Setup() => _f = new AccessRecordingFixture(Format);

    [GlobalCleanup]
    public void Cleanup() => _f.Dispose();

    // Outside a body: the provider is installed and no attempt runs.

    [Benchmark(OperationsPerInvoke = Ops)]
    public int Read_Outside() => AccessRecordingScenarios.ReadDistinct(_f);

    [Benchmark(OperationsPerInvoke = Ops)]
    public int WriteValue_Outside()
    {
        AccessRecordingScenarios.WriteValue(_f);
        return 0;
    }

    [Benchmark(OperationsPerInvoke = Ops)]
    public int WriteDictionary_Outside()
    {
        AccessRecordingScenarios.WriteDictionary(_f);
        _f.ResetJournals();
        return 0;
    }

    // In a recording body, staged, journaled and removed. Per access.

    [Benchmark(OperationsPerInvoke = Ops)]
    public int Read_Recorded() => _f.Record(AccessRecordingScenarios.ReadDistinct);

    [Benchmark(OperationsPerInvoke = Ops)]
    public int ReadSameKey_Recorded() => _f.Record(AccessRecordingScenarios.ReadSameKey);

    [Benchmark(OperationsPerInvoke = Ops)]
    public int ReadValue_Recorded() => _f.Record(AccessRecordingScenarios.ReadValue);

    /// <summary>Per iteration: a read of one key and a write of another key of the same dictionary.</summary>
    [Benchmark(OperationsPerInvoke = Ops)]
    public int ReadAWriteB_Recorded() => _f.Record(AccessRecordingScenarios.ReadAWriteB);

    [Benchmark(OperationsPerInvoke = Ops)]
    public int WriteValue_Recorded() => _f.Record(AccessRecordingScenarios.WriteValue);

    [Benchmark(OperationsPerInvoke = Ops)]
    public int WriteDictionary_Recorded() => _f.Record(AccessRecordingScenarios.WriteDictionary);

    /// <summary>Per read of <c>Keys</c>, 64 keys, of an unchanged dictionary.</summary>
    [Benchmark(OperationsPerInvoke = AccessRecordingScenarios.KeysReads)]
    public int Keys_Recorded() => _f.Record(AccessRecordingScenarios.Keys);

    // In a replaying body. Per access.

    [Benchmark(OperationsPerInvoke = Ops)]
    public int Read_Replayed() => _f.Replay(AccessRecordingScenario.ReadDistinct);

    [Benchmark(OperationsPerInvoke = Ops)]
    public int ReadSameKey_Replayed() => _f.Replay(AccessRecordingScenario.ReadSameKey);

    [Benchmark(OperationsPerInvoke = Ops)]
    public int ReadValue_Replayed() => _f.Replay(AccessRecordingScenario.ReadValue);

    [Benchmark(OperationsPerInvoke = Ops)]
    public int ReadAWriteB_Replayed() => _f.Replay(AccessRecordingScenario.ReadAWriteB);

    [Benchmark(OperationsPerInvoke = Ops)]
    public int WriteValue_Replayed() => _f.Replay(AccessRecordingScenario.WriteValue);

    // Per body: a typical step (3 reads, 3 writes, 1 count), as one attempt which journals and completes.

    [Benchmark(OperationsPerInvoke = Bodies)]
    public int TypicalBody_Recorded() => _f.RecordBodies(AccessRecordingScenarios.TypicalBody, Bodies);

    [Benchmark(OperationsPerInvoke = Bodies)]
    public int TypicalBody_Replayed() => _f.ReplayBodies(AccessRecordingScenario.TypicalBody, Bodies);
}
