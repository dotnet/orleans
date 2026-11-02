using System.Runtime.CompilerServices;

namespace Orleans.Messaging.Tests.Support;

internal static class InboxTestAccess
{

    public static IEnumerable<InboxMessage> Messages(this Inbox inbox) => inbox.MessageState.Values.Select(static entry => entry.Message);

    public static int Count(this IOutbox outbox) => outbox switch
    {
        JournaledTestOutbox value => value.Count,
        _ => throw new NotSupportedException(outbox.GetType().FullName)
    };

    public static IEnumerable<OutboxMessage> Messages(this IOutbox outbox) => outbox switch
    {
        JournaledTestOutbox value => value.Messages,
        _ => throw new NotSupportedException(outbox.GetType().FullName)
    };

    public static int GetProcessedCount(Inbox inbox) => GetProcessed(inbox).Count;

    public static HashSet<MessageId> GetProcessedKeys(Inbox inbox) => [.. GetProcessed(inbox).Keys];

    public static void AddProcessed(Inbox inbox, MessageId messageId, DateTimeOffset sentAt) =>
        GetProcessed(inbox).Add(messageId, new() { MessageId = messageId, SentAt = sentAt });

    public static IReadOnlyDictionary<MessageId, InboxEntry> GetPendingMessages(Inbox inbox) => inbox.MessageState;

    public static DateTimeOffset GetProcessedSentAt(Inbox inbox, MessageId messageId) =>
        GetProcessed(inbox)[messageId].SentAt;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_admissionFloor")]
    public static extern ref DateTimeOffset AdmissionFloor(Inbox inbox);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_durableAdmissionFloor")]
    public static extern ref DateTimeOffset DurableAdmissionFloor(Inbox inbox);

    public static InboxMessageState GetMessageState(Inbox inbox, MessageId messageId) =>
        inbox.MessageState[messageId].State;

    public static void StageAcceptance(Inbox inbox, InboxMessage message) =>
        Emit(inbox, new() { Kind = InboxCommandKind.Accept, Message = message });

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Emit")]
    private static extern void Emit(Inbox inbox, InboxCommand command);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_wakeupJobId")]
    public static extern ref string? WakeupJobId(MessagingPump pump);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_messages")]
    private static extern ref Dictionary<MessageId, InboxEntry> GetMessages(Inbox inbox);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_processed")]
    private static extern ref Dictionary<MessageId, InboxProcessedEntry> GetProcessed(Inbox inbox);
}
