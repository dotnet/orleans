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

    public static HashSet<HierarchicalKey> GetProcessedKeys(Inbox inbox) => [.. GetProcessed(inbox).Keys];

    public static bool TryGetProcessedAt(Inbox inbox, HierarchicalKey messageId, out DateTimeOffset processedAt) =>
        GetProcessed(inbox).TryGetValue(messageId, out processedAt);

    public static void SetProcessedAt(Inbox inbox, HierarchicalKey messageId, DateTimeOffset processedAt) =>
        GetProcessed(inbox)[messageId] = processedAt;

    public static IReadOnlyDictionary<HierarchicalKey, InboxEntry> GetPendingMessages(Inbox inbox) => inbox.MessageState;

    public static IReadOnlyDictionary<HierarchicalKey, DateTimeOffset> GetProcessedMessages(Inbox inbox) => GetProcessed(inbox);

    public static InboxMessageState GetMessageState(Inbox inbox, HierarchicalKey messageId) =>
        inbox.MessageState[messageId].State;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_messages")]
    private static extern ref Dictionary<HierarchicalKey, InboxEntry> GetMessages(Inbox inbox);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_processed")]
    private static extern ref Dictionary<HierarchicalKey, DateTimeOffset> GetProcessed(Inbox inbox);
}
