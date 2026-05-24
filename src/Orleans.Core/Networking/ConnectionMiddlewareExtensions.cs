using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Messaging;

namespace Orleans
{
    /// <summary>
    /// Extension methods for registering <see cref="IConnectionMiddleware"/> in the connection pipeline.
    /// </summary>
    public static class ConnectionMiddlewareExtensions
    {
        /// <summary>
        /// Adds an <see cref="IConnectionMiddleware"/> to the connection pipeline.
        /// The middleware is resolved from DI.
        /// </summary>
        /// <typeparam name="T">The middleware type implementing <see cref="IConnectionMiddleware"/>.</typeparam>
        public static IConnectionBuilder UseMiddleware<T>(this IConnectionBuilder builder) where T : IConnectionMiddleware
        {
            builder.Use(next =>
            {
                var middleware = ActivatorUtilities.CreateInstance<T>(builder.ApplicationServices, next);
                return context => middleware.OnConnectionAsync(context, next);
            });

            return builder;
        }

        /// <summary>
        /// Adds an <see cref="IConnectionMiddleware"/> instance to the connection pipeline.
        /// </summary>
        public static IConnectionBuilder UseMiddleware(this IConnectionBuilder builder, IConnectionMiddleware middleware)
        {
            if (middleware is null)
            {
                throw new ArgumentNullException(nameof(middleware));
            }

            builder.Use(next =>
            {
                return context => middleware.OnConnectionAsync(context, next);
            });

            return builder;
        }
    }
}
