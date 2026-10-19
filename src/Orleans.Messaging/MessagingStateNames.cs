namespace Orleans.Messaging;

internal static class MessagingStateNames
{
    private const string Prefix = "orleans.messaging.";

    public const string Inbox = Prefix + "inbox";
    public const string Outbox = Prefix + "outbox";
}
