namespace Orleans.Messaging;

internal sealed class InboxHandlerContext(InboxMessage message, Inbox.HandlerExecution execution) : IInboxHandlerContext
{
    public InboxMessage Message { get; } = message;

    public void Complete() => execution.Complete();

    public void Fail(string reason) => execution.Fail(reason);
}
