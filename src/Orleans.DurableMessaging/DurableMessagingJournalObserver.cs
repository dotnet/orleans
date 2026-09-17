using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal sealed partial class DurableMessagingJournalObserver(
    DurableInboxExtension inbox,
    [FromKeyedServices(DurableMessagingStateNames.OutboxObserver)] DurableMessagingJournalEndpoint outbox,
    ILogger<DurableMessagingJournalObserver> logger) : IJournaledStateObserver
{
    public void OnWriteRequested()
    {
        inbox.OnWriteRequested();
        outbox.Observer.OnWriteRequested();
    }

    public void OnDeleteRequested()
    {
        inbox.OnDeleteRequested();
        outbox.Observer.OnDeleteRequested();
    }

    public async ValueTask OnWritePreparingAsync(CancellationToken cancellationToken)
    {
        await inbox.OnWritePreparingAsync(cancellationToken).ConfigureAwait(true);
        await outbox.Observer.OnWritePreparingAsync(cancellationToken).ConfigureAwait(true);
    }

    public ValueTask OnWriteFinalizingAsync(CancellationToken cancellationToken)
    {
        inbox.FinalizeWrite(cancellationToken);
        outbox.FinalizeWrite(cancellationToken);
        return default;
    }

    public async ValueTask OnDeletePreparingAsync(CancellationToken cancellationToken)
    {
        await inbox.OnDeletePreparingAsync(cancellationToken).ConfigureAwait(true);
        await outbox.Observer.OnDeletePreparingAsync(cancellationToken).ConfigureAwait(true);
    }

    public void OnRecoveryStarted() => Notify(static observer => observer.OnRecoveryStarted(), nameof(OnRecoveryStarted));
    public void OnRecoveryCompleted() => Notify(static observer => observer.OnRecoveryCompleted(), nameof(OnRecoveryCompleted));
    public void OnDeleteCompleted() => Notify(static observer => observer.OnDeleteCompleted(), nameof(OnDeleteCompleted));
    public void OnWriteStarted() => Notify(static observer => observer.OnWriteStarted(), nameof(OnWriteStarted));
    public void OnWriteCompleted() => Notify(static observer => observer.OnWriteCompleted(), nameof(OnWriteCompleted));
    public void OnFaulted(Exception exception) => Notify(observer => observer.OnFaulted(exception), nameof(OnFaulted));

    private void Notify(Action<IJournaledStateObserver> callback, string name)
    {
        Notify(inbox, callback, name);
        Notify(outbox.Observer, callback, name);
    }

    private void Notify(IJournaledStateObserver observer, Action<IJournaledStateObserver> callback, string name)
    {
        try
        {
            callback(observer);
        }
        catch (Exception exception)
        {
            LogNotificationError(logger, exception, name, observer.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Durable messaging notification {Notification} failed for {Observer}")]
    private static partial void LogNotificationError(ILogger logger, Exception exception, string notification, string observer);
}
