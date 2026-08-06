using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// Interface for connection middleware.
    /// <para>
    /// Implementers provide <see cref="OnConnectionAsync"/> and invoke the supplied
    /// <see cref="ConnectionDelegate"/> to continue the pipeline.
    /// </para>
    /// <para>
    /// Register via <c>builder.UseMiddleware()</c> or manually with
    /// <c>builder.Use(next =&gt; ctx =&gt; middleware.OnConnectionAsync(ctx, next))</c>.
    /// </para>
    /// </summary>
    public interface IConnectionMiddleware
    {
        /// <summary>
        /// Called when a connection is established. Implementations should call
        /// <paramref name="next"/> to continue the pipeline after performing their work.
        /// </summary>
        /// <param name="context">The connection context.</param>
        /// <param name="next">The next delegate in the connection pipeline.</param>
        Task OnConnectionAsync(ConnectionContext context, ConnectionDelegate next);
    }
}
