using System;

namespace Orleans.DurableMessaging;

internal sealed class InboxHandlerContext(DurableEnvelope envelope, Action complete) : IInboxHandlerContext
{
    private readonly Action _complete = complete ?? throw new ArgumentNullException(nameof(complete));

    public DurableEnvelope Envelope { get; } = envelope;

    public void Complete() => _complete();
}
