using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Messaging;

/// <summary>Handles inbox messages which it accepts.</summary>
/// <remarks>
/// The inbox dispatches each message to the first registered handler whose <see cref="CanHandle"/> returns
/// <see langword="true"/>.
/// </remarks>
public interface IInboxHandler
{
    /// <summary>Determines whether this handler handles the message.</summary>
    /// <param name="context">The message and its terminal operations.</param>
    /// <returns><see langword="true"/> when this handler handles the message.</returns>
    bool CanHandle(IInboxHandlerContext context);

    /// <summary>Handles the message.</summary>
    /// <param name="context">The message and its terminal operations.</param>
    /// <param name="cancellationToken">The attempt token.</param>
    /// <returns>A task which completes when handling returns.</returns>
    ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken);
}
