using System.Buffers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Journaling.Json;
using Orleans.Serialization;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Hosts managers over every kind of journaled state in one journal format, for transaction tests which run over each format.
/// </summary>
internal sealed class TransactionTestHost : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly JournaledStateManagerShared _shared;

    public TransactionTestHost(string formatKey)
    {
        FormatKey = formatKey;
        var builder = new TestSiloBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSerializer();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.AddJournaling();
        if (formatKey == JsonLinesJournalFormat.JournalFormatKey)
        {
            builder.UseJsonJournalFormat(JournalingTestsJsonContext.Default);
        }

        _services = builder.Services.BuildServiceProvider();
        _shared = new JournaledStateManagerShared(
            _services.GetRequiredService<ILogger<JournaledStateManager>>(),
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = formatKey }),
            TimeProvider.System,
            _services);
    }

    /// <summary>Gets the journal formats which transaction tests run over.</summary>
    public static TheoryData<string> Formats => new(OrleansBinaryJournalFormat.JournalFormatKey, JsonLinesJournalFormat.JournalFormatKey);

    public string FormatKey { get; }

    /// <summary>Creates a manager over <paramref name="storage"/> with one state of each kind, and recovers it.</summary>
    /// <param name="storage">The journal's storage.</param>
    /// <param name="extraState">Creates another state, registered as <c>extra</c>, from the manager, its shared options and services.</param>
    public async Task<TransactionActivation> ActivateAsync(
        IJournalStorage storage,
        Func<JournaledStateManager, JournaledStateManagerShared, IServiceProvider, object>? extraState = null)
    {
        var manager = new JournaledStateManager(_shared, storage);
        var activation = new TransactionActivation(
            manager,
            new DurableDictionary<string, string>("dictionary", manager, _shared, _services),
            new DurableValue<string>("value", manager, _shared, _services),
            new DurableList<string>("list", manager, _shared, _services),
            new DurableQueue<string>("queue", manager, _shared, _services),
            new DurableSet<string>("set", manager, _shared, _services),
            new DurableTaskCompletionSource<int>(
                "completion",
                manager,
                _shared,
                _services,
                _services.GetRequiredService<DeepCopier<int>>(),
                _services.GetRequiredService<DeepCopier<Exception>>()),
            new DurableLog<string>("log", manager, _shared, _services))
        {
            Extra = extraState?.Invoke(manager, _shared, _services),
        };

        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        return activation;
    }

    public void Dispose() => _services.Dispose();

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

/// <summary>One manager with one state of each kind.</summary>
internal sealed record TransactionActivation(
    JournaledStateManager Manager,
    DurableDictionary<string, string> Dictionary,
    DurableValue<string> Value,
    DurableList<string> List,
    DurableQueue<string> Queue,
    DurableSet<string> Set,
    DurableTaskCompletionSource<int> Completion,
    DurableLog<string> Log)
{
    /// <summary>Gets the state which the activation's creator added, if any.</summary>
    public object? Extra { get; init; }

    /// <summary>Gets the content of every state, for comparison with a model.</summary>
    public string Capture()
    {
        var completion = Completion.State;
        return string.Join(
            '|',
            string.Join(',', Dictionary.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}={pair.Value}")),
            Value.Value,
            string.Join(',', List),
            string.Join(',', Queue),
            string.Join(',', Set.Order(StringComparer.Ordinal)),
            $"{completion.Status}:{(completion.Status is DurableTaskCompletionSourceStatus.Completed ? completion.Value : 0)}",
            string.Join(',', Log));
    }
}

/// <summary>
/// Storage which holds appends at a gate, fails the next append, requests compaction on demand, and counts its operations.
/// </summary>
internal sealed class ControlledJournalStorage(string formatKey) : IJournalStorage
{
    private readonly object _lock = new();
    private TaskCompletionSource? _gate;
    private TaskCompletionSource _appendHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _failNextAppend;
    private int _appends;
    private int _replaces;

    public VolatileJournalStorage Inner { get; } = new(formatKey);

    public bool RequestCompaction { get; set; }

    /// <summary>Gets the number of appends which reached storage.</summary>
    public int Appends => Volatile.Read(ref _appends);

    /// <summary>Gets the number of replacements which reached storage.</summary>
    public int Replaces => Volatile.Read(ref _replaces);

    public bool IsCompactionRequested => RequestCompaction || Inner.IsCompactionRequested;

    /// <summary>Holds later appends until <see cref="ReleaseAppends"/>.</summary>
    /// <returns>A task which completes once an append waits at the gate.</returns>
    public Task HoldAppends()
    {
        lock (_lock)
        {
            _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _appendHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _appendHeld.Task;
        }
    }

    public void ReleaseAppends()
    {
        TaskCompletionSource? gate;
        lock (_lock)
        {
            gate = _gate;
            _gate = null;
        }

        gate?.SetResult();
    }

    /// <summary>Fails the next append with <paramref name="exception"/>, before it changes storage.</summary>
    public void FailNextAppend(Exception exception)
    {
        lock (_lock)
        {
            _failNextAppend = exception;
        }
    }

    public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => Inner.ReadAsync(consumer, cancellationToken);

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        TaskCompletionSource? gate;
        Exception? failure;
        lock (_lock)
        {
            failure = _failNextAppend;
            _failNextAppend = null;
            gate = _gate;
            if (gate is not null)
            {
                _appendHeld.TrySetResult();
            }
        }

        if (failure is not null)
        {
            throw failure;
        }

        if (gate is not null)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        await Inner.AppendAsync(value, cancellationToken);
        Interlocked.Increment(ref _appends);
    }

    public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _replaces);
        RequestCompaction = false;
        return Inner.ReplaceAsync(value, cancellationToken);
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken) => Inner.DeleteAsync(cancellationToken);
}

/// <summary>
/// A state which takes no part in routing: it journals one entry per increment and counts the entries it replays.
/// </summary>
internal sealed class CountingState : IStateMachine
{
    private JournalStreamWriter _writer;

    public CountingState(IJournaledStateManager manager) => manager.RegisterStateMachine("counting", this);

    public int Count { get; private set; }

    public void Increment()
    {
        WriteEntry(_writer);
        Count++;
    }

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context)
    {
        entry.Reader.Skip(entry.Reader.Length);
        Count++;
    }

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _writer = writer;
        Count = 0;
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
    }

    void IStateMachine.WriteSnapshot(JournalStreamWriter writer)
    {
        for (var i = 0; i < Count; i++)
        {
            WriteEntry(writer);
        }
    }

    private static void WriteEntry(JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        entry.Writer.Write("1"u8);
        entry.Commit();
    }
}
