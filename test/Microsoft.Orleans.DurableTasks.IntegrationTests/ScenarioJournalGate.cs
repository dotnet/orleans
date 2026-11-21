using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Durable.Journaling;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>Holds one real journal write immediately before admission or after storage acknowledges it.</summary>
internal sealed class ScenarioJournalGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _target;
    private bool _afterAcknowledgement;
    private int _claimed;

    public Task Entered => _entered.Task;
    public bool IsReplacement { get; private set; }

    public void Arm(GrainId target, bool afterAcknowledgement)
    {
        _afterAcknowledgement = afterAcknowledgement;
        _target = JournalId.FromGrainId(target).Value;
    }

    public void Release() => _released.TrySetResult();

    public void Configure(ISiloBuilder silo)
    {
        var registration = silo.Services.Last(static descriptor => descriptor.ServiceType == typeof(IJournalStorageProvider));
        var factory = registration.ImplementationFactory
            ?? throw new InvalidOperationException("The scenario fixture registers journal storage with a factory.");
        silo.Services.RemoveAll<IJournalStorageProvider>();
        silo.Services.AddSingleton<IJournalStorageProvider>(services =>
            new Provider((IJournalStorageProvider)factory(services), this));
    }

    private bool Claim(string journal) =>
        string.Equals(journal, _target, StringComparison.Ordinal) && Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

    private async ValueTask HoldAsync(CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
    }

    private sealed class Provider(IJournalStorageProvider inner, ScenarioJournalGate gate) : IJournalStorageProvider
    {
        public IJournalStorage CreateStorage(JournalId journalId) =>
            new Storage(inner.CreateStorage(journalId), journalId.Value, gate);
    }

    private sealed class Storage(IJournalStorage inner, string journal, ScenarioJournalGate gate) : IJournalStorage
    {
        public bool IsCompactionRequested => inner.IsCompactionRequested;
        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) =>
            inner.ReadAsync(consumer, cancellationToken);
        public ValueTask<bool> CreateIfNotExistsAsync(
            IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
            inner.CreateIfNotExistsAsync(metadata, cancellationToken);
        public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.GetMetadataAsync(cancellationToken);
        public ValueTask<IJournalMetadata?> UpdateMetadataAsync(
            IReadOnlyDictionary<string, string>? set = null, IEnumerable<string>? remove = null,
            string? expectedETag = null, CancellationToken cancellationToken = default) =>
            inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);
        public ValueTask DeleteAsync(CancellationToken cancellationToken) => inner.DeleteAsync(cancellationToken);
        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) =>
            WriteAsync(value, replace: false, cancellationToken);
        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) =>
            WriteAsync(value, replace: true, cancellationToken);

        private async ValueTask WriteAsync(ReadOnlySequence<byte> value, bool replace, CancellationToken cancellationToken)
        {
            var hold = gate.Claim(journal);
            if (hold) gate.IsReplacement = replace;
            if (hold && !gate._afterAcknowledgement) await gate.HoldAsync(cancellationToken);
            if (replace) await inner.ReplaceAsync(value, cancellationToken);
            else await inner.AppendAsync(value, cancellationToken);
            if (hold && gate._afterAcknowledgement) await gate.HoldAsync(cancellationToken);
        }
    }
}
