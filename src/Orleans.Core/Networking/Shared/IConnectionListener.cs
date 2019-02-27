using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Networking.Shared
{
    /// <remarks>
    /// This is a duplicate of https://github.com/aspnet/AspNetCore/blob/master/src/Servers/Connections.Abstractions/src/IConnectionListener.cs
    /// </remarks>
    internal interface IConnectionListener
    {
        /// <summary>
        /// The endpoint that was bound. This may differ from the requested endpoint, such as when the caller requested that any free port be selected.
        /// </summary>
        EndPoint EndPoint { get; }

        /// <summary>
        /// Begins an asynchronous operation to accept an incoming connection.
        /// </summary>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A <see cref="ValueTask{ConnectionContext}"/> that completes when a connection is accepted, yielding the <see cref="ConnectionContext" /> representing the connection.</returns>
        ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops listening for incoming connections.
        /// </summary>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A <see cref="ValueTask"/> that represents the un-bind operation.</returns>
        ValueTask UnbindAsync(CancellationToken cancellationToken = default);

        ValueTask DisposeAsync();
    }
}
