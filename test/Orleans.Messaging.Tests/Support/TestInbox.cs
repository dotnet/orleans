namespace Orleans.Messaging.Tests.Support;

/// <summary>An <see cref="IInbox"/> which dispatches directly to its registered handlers.</summary>
internal sealed class TestInbox : IInbox
{
    private readonly InboxDispatcher _dispatcher = new([]);

    public bool IsEmpty => _dispatcher.IsEmpty;

    public IInbox Register(IInboxHandler handler)
    {
        _dispatcher.Add(handler);
        return this;
    }

    public IInbox Register(IInboxMessageSink sink) => throw new NotSupportedException("The test inbox dispatches to handlers only.");

    public IInbox Register(IInboxWorkSource source) => throw new NotSupportedException("The test inbox dispatches to handlers only.");

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
        _dispatcher.HandleAsync(context, cancellationToken);
}

internal static class TestInboxExtensions
{
    public static ValueTask HandleAsync(this IInbox inbox, IInboxHandlerContext context, CancellationToken cancellationToken) =>
        ((TestInbox)inbox).HandleAsync(context, cancellationToken);
}
