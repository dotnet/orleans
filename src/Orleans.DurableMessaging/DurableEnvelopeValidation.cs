using System;
using System.Text;

namespace Orleans.DurableMessaging;

internal static class DurableEnvelopeValidation
{
    internal const int MaxMessageIdBytes = 1024;
    internal const int MaxMessageIdSegments = 32;
    internal const int MaxSubjectBytes = 256;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static void Validate(DurableEnvelope envelope)
    {
        if (envelope.MessageId.IsDefault)
        {
            throw new ArgumentException("The envelope message ID must not be unset.", nameof(envelope));
        }
        if (envelope.SenderId.IsDefault)
        {
            throw new ArgumentException("The envelope sender must not be the default grain ID.", nameof(envelope));
        }
        if (envelope.ReceiverId.IsDefault)
        {
            throw new ArgumentException("The envelope receiver must not be the default grain ID.", nameof(envelope));
        }
        if (envelope.MessageId.SegmentCount > MaxMessageIdSegments)
        {
            throw new ArgumentException($"The envelope message ID exceeds {MaxMessageIdSegments} segments.", nameof(envelope));
        }
        if (Utf8.GetByteCount(envelope.MessageId.ToString()) > MaxMessageIdBytes)
        {
            throw new ArgumentException($"The envelope message ID exceeds {MaxMessageIdBytes} UTF-8 bytes.", nameof(envelope));
        }
        ValidateSubject(envelope.Subject);
    }

    public static void ValidateSubject(string subject)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        if (Utf8.GetByteCount(subject) > MaxSubjectBytes)
        {
            throw new ArgumentException($"The subject exceeds {MaxSubjectBytes} UTF-8 bytes.", nameof(subject));
        }
    }
}
