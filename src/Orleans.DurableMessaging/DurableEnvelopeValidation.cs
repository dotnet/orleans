using System;

namespace Orleans.DurableMessaging;

internal static class DurableEnvelopeValidation
{
    public static void Validate(DurableEnvelope envelope)
    {
        if (envelope.MessageId == Guid.Empty)
        {
            throw new ArgumentException("The envelope message ID must not be empty.", nameof(envelope));
        }
        if (envelope.SenderId.IsDefault)
        {
            throw new ArgumentException("The envelope sender must not be the default grain ID.", nameof(envelope));
        }
        if (envelope.Payload is null)
        {
            throw new ArgumentException("The envelope payload must be provided.", nameof(envelope));
        }
    }
}
