namespace Benchmarks.Journaling;

/// <summary>
/// Reports the journal bytes which a durable task body's access records take, per access and per body, in each journal
/// format: the entries which journal writes append to <c>$task-access-logs</c>.
/// </summary>
internal static class AccessRecordingBytes
{
    public static void Run()
    {
        Console.WriteLine("| Item | Format | Bytes |");
        Console.WriteLine("|---|---|---:|");
        foreach (var format in AccessRecordingFixture.Formats)
        {
            using var f = new AccessRecordingFixture(format);
            var ops = AccessRecordingBenchmarks.Ops;
            Report(f, format, "Read of a 32-character value, the attempt's only access", 1, f => f.Dictionary.TryGetValue(f.Keys[0], out var v) ? v.Length : 0);
            Report(f, format, $"Each further read of another key (of {ops})", ops, AccessRecordingScenarios.ReadDistinct, subtractSingle: true, perOp: true);
            Report(f, format, $"Each read of the same key of an unchanged dictionary (of {ops})", ops, AccessRecordingScenarios.ReadSameKey, perOp: true);
            Report(f, format, $"{ops} reads of an unchanged value, in total", 1, AccessRecordingScenarios.ReadValue);
            Report(f, format, $"Each iteration of read key A, write key B (of {ops})", ops, AccessRecordingScenarios.ReadAWriteB, perOp: true);
            Report(f, format, $"Each result-less value write (of {ops})", ops, AccessRecordingScenarios.WriteValue, perOp: true);
            Report(f, format, $"Each read of 64 keys of an unchanged dictionary (of {AccessRecordingScenarios.KeysReads})", AccessRecordingScenarios.KeysReads, AccessRecordingScenarios.Keys, perOp: true);
            Report(f, format, "Typical body: 3 reads, 3 writes, 1 count", 1, AccessRecordingScenarios.TypicalBody);
            Console.WriteLine($"| Removal at completion | {format} | {f.MeasureBytes(AccessRecordingScenarios.TypicalBody).Removal} |");
        }
    }

    private static void Report(AccessRecordingFixture f, string format, string item, int count, Func<AccessRecordingFixture, int> body, bool subtractSingle = false, bool perOp = false)
    {
        double bytes = f.MeasureBytes(body).Records;
        if (subtractSingle)
        {
            bytes -= f.MeasureBytes(static f => f.Dictionary.TryGetValue(f.Keys[0], out var v) ? v.Length : 0).Records;
            bytes /= count - 1;
        }
        else if (perOp)
        {
            bytes /= count;
        }

        Console.WriteLine($"| {item} | {format} | {bytes:0.##} |");
    }
}
