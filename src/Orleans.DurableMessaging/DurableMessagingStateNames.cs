namespace Orleans.DurableMessaging;

internal static class DurableMessagingStateNames
{
    private const string Prefix = "__orleans.durable-messaging.";

    public const string Inbox = Prefix + "inbox";
    public const string InboxProcessed = Prefix + "inbox-processed";
    public const string InboxMessageState = Prefix + "inbox-message-state";
    public const string InboxDeadLetters = Prefix + "inbox-dead-letters";
    public const string InboxJobId = Prefix + "inbox-job-id";
    public const string InboxCompletedJobId = Prefix + "inbox-completed-job-id";
    public const string InboxJobSequence = Prefix + "inbox-job-sequence";
    public const string Outbox = Prefix + "outbox";
    public const string OutboxMessageState = Prefix + "outbox-message-state";
    public const string OutboxDeadLetters = Prefix + "outbox-dead-letters";
    public const string OutboxJobId = Prefix + "outbox-job-id";
    public const string OutboxCompletedJobId = Prefix + "outbox-completed-job-id";
    public const string OutboxJobSequence = Prefix + "outbox-job-sequence";
}
