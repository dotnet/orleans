using System.Runtime.CompilerServices;

namespace Orleans.Messaging.Tests.Support;

internal static class InboxTestAccess
{

    public static IEnumerable<InboxMessage> Messages(this Inbox inbox) => inbox.MessageState.Values.Select(static entry => entry.Message);

    public static int Count(this IOutbox outbox) => outbox switch
    {
        Outbox value => value.Count,
        JournaledTestOutbox value => value.Count,
        _ => throw new NotSupportedException(outbox.GetType().FullName)
    };

    public static IEnumerable<OutboxMessage> Messages(this IOutbox outbox) => outbox switch
    {
        Outbox value => value.MessageState.Values.Select(static entry => entry.Message),
        JournaledTestOutbox value => value.Messages,
        _ => throw new NotSupportedException(outbox.GetType().FullName)
    };

    public static int GetProcessedCount(Inbox inbox) => GetProcessed(inbox).Count;

    public static HashSet<MessageId> GetProcessedKeys(Inbox inbox) => [.. GetProcessed(inbox).Keys];

    public static bool TryGetProcessedAt(Inbox inbox, MessageId messageId, out DateTimeOffset processedAt) =>
        GetProcessed(inbox).TryGetValue(messageId, out processedAt);

    public static void SetProcessedAt(Inbox inbox, MessageId messageId, DateTimeOffset processedAt) =>
        GetProcessed(inbox)[messageId] = processedAt;

    public static IReadOnlyDictionary<MessageId, InboxEntry> GetPendingMessages(Inbox inbox) => inbox.MessageState;

    public static IReadOnlyDictionary<MessageId, DateTimeOffset> GetProcessedMessages(Inbox inbox) => GetProcessed(inbox);

    public static InboxMessageState GetMessageState(Inbox inbox, MessageId messageId) =>
        inbox.MessageState[messageId].State;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_wakeupJobId")]
    public static extern ref string? WakeupJobId(MessagingPump pump);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_messages")]
    private static extern ref Dictionary<MessageId, InboxEntry> GetMessages(Inbox inbox);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_processed")]
    private static extern ref Dictionary<MessageId, DateTimeOffset> GetProcessed(Inbox inbox);
}
