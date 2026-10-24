using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Orleans.Messaging.Tests.Support;

public interface IPersistentStateMessagingGrain : IGrainWithGuidKey
{
    Task<PersistentStateMessagingSnapshot> GetSnapshotAsync();
}

[GenerateSerializer]
public sealed record PersistentStateMessagingSnapshot(
    [property: Id(0)] int InboxCount,
    [property: Id(1)] int Handled,
    [property: Id(2)] string[] DeadLetterReasons,
    [property: Id(3)] int[] DeadLetterAttempts);

// A messaging grain which also holds journaled IPersistentState, whose in-place changes the journal can't observe.
public sealed class PersistentStateMessagingGrain : Grain, IPersistentStateMessagingGrain, IMessagingGrain, IInboxHandler
{
    private static readonly ConcurrentDictionary<GrainId, int> Handled = new();
    private readonly IInbox _inbox;
    private readonly IMessagingDiagnostics _diagnostics;

    public PersistentStateMessagingGrain(
        IInbox inbox,
        IMessagingDiagnostics diagnostics,
        [FromKeyedServices("ledger")] IPersistentState<List<int>> ledger)
    {
        _inbox = inbox;
        _diagnostics = diagnostics;
        _ = ledger;
        inbox.Register(this);
    }

    public Task<PersistentStateMessagingSnapshot> GetSnapshotAsync() => Task.FromResult(new PersistentStateMessagingSnapshot(
        ((Inbox)_inbox).Count,
        Handled.GetValueOrDefault(this.GetGrainId()),
        _diagnostics.InboxDeadLetters.Select(static letter => letter.Reason).ToArray(),
        _diagnostics.InboxDeadLetters.Select(static letter => letter.AttemptCount).ToArray()));

    public bool CanHandle(InboxMessage message) => true;

    // Fails before touching any state, which the inbox can't distinguish from a failure after an in-place change.
    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        Handled.AddOrUpdate(this.GetGrainId(), 1, static (_, count) => count + 1);
        throw new InvalidOperationException("Injected handler failure before any state change.");
    }
}
