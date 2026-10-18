using System;
using System.Text;

namespace Orleans.Messaging;

internal static class EnvelopeValidation
{
    internal const int MaxMessageIdBytes = 1024;
    internal const int MaxSubjectBytes = 256;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    // External admission and recovery validate generated values before publishing shared mutations.
    public static void Validate(Envelope envelope)
    {
        ValidateMessageId(envelope.MessageId);
        if (envelope.Payload is null) throw new ArgumentException("The payload must not be null.", nameof(envelope));
        if (envelope.Subject is not null) ValidateSubject(envelope.Subject);
        if (envelope.Headers is { } headers)
        {
            foreach (var header in headers)
            {
                if (string.IsNullOrEmpty(header.Key)) throw new ArgumentException("Header keys must not be empty.", nameof(envelope));
                if (!HierarchicalKey.IsWellFormedUtf16(header.Key))
                    throw new ArgumentException("Header keys must not contain unpaired surrogate characters.", nameof(envelope));
                if (header.Value is null) throw new ArgumentException("Header values must not be null.", nameof(envelope));
            }
        }
    }

    public static void Validate(InboxMessage message) => Validate(message.Envelope);

    public static void Validate(OutboxMessage message)
    {
        if (message.ReceiverId.IsDefault) throw new ArgumentException("The outgoing receiver must not be default.", nameof(message));
        Validate(message.Envelope);
    }

    internal static void ValidateMessageId(MessageId messageId)
    {
        if (messageId.IsDefault) throw new ArgumentException("The message ID must not be unset.", nameof(messageId));
        if (Utf8.GetByteCount(messageId.ToString()) > MaxMessageIdBytes)
            throw new ArgumentException($"The message ID exceeds {MaxMessageIdBytes} UTF-8 bytes.", nameof(messageId));
    }

    public static void ValidateSubject(string subject)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        if (Utf8.GetByteCount(subject) > MaxSubjectBytes)
            throw new ArgumentException($"The subject exceeds {MaxSubjectBytes} UTF-8 bytes.", nameof(subject));
    }
}
