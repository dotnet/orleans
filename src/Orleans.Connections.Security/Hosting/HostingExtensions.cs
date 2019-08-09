using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Connections.Security;

namespace Orleans
{
    public static class TlsConnectionBuilderExtensions
    {
        public static void UseServerTls(
            this IConnectionBuilder builder,
            TlsOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var loggerFactory = builder.ApplicationServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory ?? NullLoggerFactory.Instance;
            builder.Use(next =>
            {
                var middleware = new TlsServerConnectionMiddleware(next, options, loggerFactory);
                return middleware.OnConnectionAsync;
            });
        }

        public static void UseClientTls(
            this IConnectionBuilder builder,
            TlsOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var loggerFactory = builder.ApplicationServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory ?? NullLoggerFactory.Instance;
            builder.Use(next =>
            {
                var middleware = new TlsClientConnectionMiddleware(next, options, loggerFactory);
                return middleware.OnConnectionAsync;
            });
        }
    }
}
