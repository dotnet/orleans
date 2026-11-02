using Orleans.Messaging.Diagnostics;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Fails the delivery attempts which <paramref name="matches"/> selects at the receiving inbox, by throwing from
/// <see cref="MessagingEvents.InboxMessageReceiving"/>: the inbox stages nothing, and the sender's outbox retries.
/// </summary>
internal sealed class DeliveryFaultInjector : IObserver<MessagingEvents.MessagingEvent>, IDisposable
{
    private readonly Func<MessagingEvents.InboxMessageReceiving, bool> _matches;
    private readonly string _error;
    private readonly IDisposable _subscription;
    private int _count;

    public DeliveryFaultInjector(Func<MessagingEvents.InboxMessageReceiving, bool> matches, string error)
    {
        (_matches, _error) = (matches, error);
        _subscription = MessagingEvents.AllEvents.Subscribe(this);
    }

    /// <summary>Gets how many delivery attempts the injector failed.</summary>
    public int Count => Volatile.Read(ref _count);

    public void OnNext(MessagingEvents.MessagingEvent value)
    {
        if (value is MessagingEvents.InboxMessageReceiving receiving && _matches(receiving))
        {
            Interlocked.Increment(ref _count);
            throw new InvalidOperationException(_error);
        }
    }

    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void Dispose() => _subscription.Dispose();
}
