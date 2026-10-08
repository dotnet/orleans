using System;

namespace Orleans.DurableMessaging;

internal sealed class InboxHandlerContext(DurableEnvelope envelope, Action complete) : IInboxHandlerContext
{
    public DurableEnvelope Envelope { get; } = envelope;

    public void Complete() => complete();
}
