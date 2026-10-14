using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Journaling.Tests;
using Xunit;
using static Orleans.Journaling.Tests.ObservationTestHelpers;

namespace Orleans.Journaling.Json.Tests;

/// <summary>
/// Tests the JSON durable log codec and observation of JSON-journaled state.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class JsonDurableLogTests
{
    private static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = JsonCodecTestJsonContext.Default };

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void JsonLogCodec_WritesAndAppliesCommands()
    {
        var codec = new JsonDurableLogCommandCodec<string>(Options);
        var handler = new DurableLogTests.RecordingLogHandler();

        Apply(codec, """["append","a"]""", writer => codec.WriteAppend(["a"], writer), handler);
        Apply(codec, """["append","b","c"]""", writer => codec.WriteAppend(["b", "c"], writer), handler);
        Apply(codec, """["trimBefore",3]""", writer => codec.WriteTrimBefore(3, writer), handler);
        Apply(codec, """["clear"]""", writer => codec.WriteClear(writer), handler);
        Apply(codec, """["seal"]""", writer => codec.WriteSeal(writer), handler);
        Apply(codec, """["snapshot",4,4,true,["d","e"]]""", writer => codec.WriteSnapshot(4, 4, isSealed: true, ["d", "e"], writer), handler);

        Assert.Equal(
            ["append a", "append b", "append c", "trim 3", "clear", "seal", "reset 4 4 True 2", "append d", "append e"],
            handler.Commands);
    }

    [Fact]
    public void JsonLogCodec_RejectsMalformedCommands()
    {
        var codec = new JsonDurableLogCommandCodec<string>(Options);
        var handler = new DurableLogTests.RecordingLogHandler();

        Assert.Throws<JsonException>(() => codec.Apply(Read("""["append"]"""), handler));
        Assert.ThrowsAny<JsonException>(() => codec.Apply(Read("""["append","a"] []"""), handler));
        Assert.Throws<JsonException>(() => codec.Apply(Read("""["trimBefore",-1]"""), handler));
        Assert.Throws<JsonException>(() => codec.Apply(Read("""["clear",1]"""), handler));
        Assert.Throws<JsonException>(() => codec.Apply(Read("""["snapshot",0,0,1,[]]"""), handler));
        Assert.Throws<JsonException>(() => codec.Apply(Read("""["snapshot",0,0,false,["a"],1]"""), handler));
        Assert.Throws<NotSupportedException>(() => codec.Apply(Read("""["rewrite"]"""), handler));
        Assert.Throws<ArgumentOutOfRangeException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteTrimBefore(-1, writer)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(1, 2, isSealed: false, [], writer)));
    }

    [Fact]
    public Task JsonJournal_ObservesAndRecoversLog() => RunOnActivationSchedulerAsync(async () =>
    {
        var services = CreateServices();
        var storage = new VolatileJournalStorage(JsonLinesJournalFormat.JournalFormatKey);
        var manager = CreateManager(services, storage);
        var log = CreateLog(services, manager);
        var value = new DurableValue<int>("value", manager, services.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(JsonLinesJournalFormat.JournalFormatKey));
        await manager.InitializeAsync(CancellationToken);

        await using var read = log.ReadAsync(0, DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var watch = value.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(DurableChangeStatus.Committed, (await watch.NextAsync()).Status);
        Assert.Equal(LogEventKind.CaughtUp, (await read.NextAsync()).Kind);

        var draft = log.BeginDraft();
        draft.Write("Hel");
        Assert.Equal(LogEventKind.DraftItem, (await read.NextAsync()).Kind);
        draft.Commit("Hello");
        Assert.Equal(LogEventKind.DraftCommitted, (await read.NextAsync()).Kind);
        Assert.Equal((LogEventKind.Entry, "Hello", DurableChangeStatus.Staged), ToTuple(await read.NextAsync()));
        value.Value = 1;
        await manager.WriteStateAsync(CancellationToken);

        Assert.Equal(new LogEvent<string> { Kind = LogEventKind.Watermark, Sequence = 1, Status = DurableChangeStatus.Committed }, await read.NextAsync());
        Assert.Equal(new ValueChange<int> { Value = 1, Status = DurableChangeStatus.Committed, Version = 1 }, await watch.NextAsync());
        Assert.Contains("""["append","Hello"]""", Encoding.UTF8.GetString(storage.Segments.Single()));

        log.TrimBefore(1);
        log.Append("World");
        await manager.WriteStateAsync(CancellationToken);

        var recovered = CreateManager(services, storage);
        var recoveredLog = CreateLog(services, recovered);
        await recovered.InitializeAsync(CancellationToken);
        Assert.Equal((1L, 2L, 2L), (recoveredLog.FirstSequence, recoveredLog.NextSequence, recoveredLog.CommittedSequence));
        Assert.Equal(["World"], recoveredLog);

        recoveredLog.Clear();
        recoveredLog.Append("Summary");
        recoveredLog.Seal();
        await recovered.WriteStateAsync(CancellationToken);

        var sealedManager = CreateManager(services, storage);
        var sealedLog = CreateLog(services, sealedManager);
        await sealedManager.InitializeAsync(CancellationToken);
        Assert.Equal((3L, 4L, true), (sealedLog.FirstSequence, sealedLog.NextSequence, sealedLog.IsSealed));
        Assert.Equal(["Summary"], sealedLog);
        await using var sealedRead = sealedLog.ReadAsync(2, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(LogEventKind.Reset, (await sealedRead.NextAsync()).Kind);
        Assert.Equal((LogEventKind.Entry, "Summary", DurableChangeStatus.Committed), ToTuple(await sealedRead.NextAsync()));
        Assert.Equal(LogEventKind.CaughtUp, (await sealedRead.NextAsync()).Kind);
        Assert.Equal(LogEventKind.Sealed, (await sealedRead.NextAsync()).Kind);
        Assert.False(await sealedRead.MoveNextAsync());

        static (LogEventKind, string?, DurableChangeStatus) ToTuple(LogEvent<string> logEvent) => (logEvent.Kind, logEvent.Item, logEvent.Status);
    });

    private static void Apply(JsonDurableLogCommandCodec<string> codec, string expectedJson, Action<JournalStreamWriter> write, IDurableLogCommandHandler<string> handler)
    {
        var payload = CodecTestHelpers.WriteEntry(write);
        Assert.Equal(expectedJson, Encoding.UTF8.GetString(payload));
        codec.Apply(CodecTestHelpers.ReadBuffer(payload), handler);
    }

    private static JournalBufferReader Read(string json) => CodecTestHelpers.ReadBuffer(Encoding.UTF8.GetBytes(json));

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<JsonJournalOptions>(options => options.SerializerOptions = Options);
        services.AddJsonJournalFormat(tryAdd: false);
        return services.BuildServiceProvider();
    }

    private static JournaledStateManager CreateManager(ServiceProvider services, IJournalStorage storage)
    {
        var shared = new JournaledStateManagerShared(
            services.GetRequiredService<ILogger<JournaledStateManager>>(),
            Microsoft.Extensions.Options.Options.Create(new JournaledStateManagerOptions { JournalFormatKey = JsonLinesJournalFormat.JournalFormatKey }),
            TimeProvider.System,
            services);
        return new JournaledStateManager(shared, storage);
    }

    private static DurableLog<string> CreateLog(ServiceProvider services, JournaledStateManager manager) =>
        new("log", manager, services.GetRequiredKeyedService<IDurableLogCommandCodec<string>>(JsonLinesJournalFormat.JournalFormatKey));
}
