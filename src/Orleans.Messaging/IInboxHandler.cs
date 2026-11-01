using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Messaging;

/// <summary>Handles inbox messages which it accepts.</summary>
/// <remarks>
/// The inbox dispatches each message to the first registered handler whose <see cref="CanHandle"/> returns
/// <see langword="true"/>. Selection receives only the message, so a handler stages a terminal outcome only
/// from <see cref="HandleAsync"/>, after the inbox selects it.
/// </remarks>
public interface IInboxHandler
{
    /// <summary>Determines whether this handler handles the message.</summary>
    /// <param name="message">The message to route.</param>
    /// <returns><see langword="true"/> when this handler handles the message.</returns>
    bool CanHandle(InboxMessage message);

    /// <summary>Handles the message.</summary>
    /// <param name="context">The message and its terminal operations.</param>
    /// <param name="cancellationToken">The attempt token.</param>
    /// <returns>A task which completes when handling returns.</returns>
    ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken);
}
