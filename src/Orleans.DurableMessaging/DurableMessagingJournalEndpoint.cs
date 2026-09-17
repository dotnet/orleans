using System;
using System.Threading;
using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal sealed class DurableMessagingJournalEndpoint(IJournaledStateObserver observer, Action<CancellationToken> finalizeWrite)
{
    public IJournaledStateObserver Observer { get; } = observer;
    public void FinalizeWrite(CancellationToken cancellationToken) => finalizeWrite(cancellationToken);
}
