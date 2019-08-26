using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Networking.Shared
{
    /// <remarks>
    /// This is a duplicate of https://github.com/aspnet/AspNetCore/blob/master/src/Servers/Connections.Abstractions/src/IConnectionListenerFactory.cs
    /// </remarks>
    internal interface IConnectionListenerFactory
    {
        /// <summary>
        /// Creates an <see cref="IConnectionListener"/> bound to the specified <see cref="EndPoint"/>.
        /// </summary>
        /// <param name="endpoint">The <see cref="EndPoint" /> to bind to.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A <see cref="ValueTask{IConnectionListener}"/> that completes when the listener has been bound, yielding a <see cref="IConnectionListener" /> representing the new listener.</returns>
        ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default);
    }
}
