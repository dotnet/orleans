using System;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Connections.Security;

namespace Orleans
{
    /// <summary>
    /// Provides extension methods for adding TLS middleware to Orleans connections.
    /// </summary>
    public static class TlsConnectionBuilderExtensions
    {
        /// <summary>
        /// Adds TLS server authentication and encryption to the connection pipeline.
        /// </summary>
        /// <param name="builder">The connection pipeline builder.</param>
        /// <param name="options">The TLS configuration.</param>
        public static void UseServerTls(
            this IConnectionBuilder builder,
            TlsOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var loggerFactory = builder.ApplicationServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory ?? NullLoggerFactory.Instance;
            builder.UseMiddleware(new TlsServerConnectionMiddleware(options, loggerFactory));
        }

        /// <summary>
        /// Adds TLS client authentication and encryption to the connection pipeline.
        /// </summary>
        /// <param name="builder">The connection pipeline builder.</param>
        /// <param name="options">The TLS configuration.</param>
        public static void UseClientTls(
            this IConnectionBuilder builder,
            TlsOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var loggerFactory = builder.ApplicationServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory ?? NullLoggerFactory.Instance;
            builder.UseMiddleware(new TlsClientConnectionMiddleware(options, loggerFactory));
        }

        internal static void ThrowNoPrivateKey(X509Certificate2 certificate, string parameterName)
        {
            throw new ArgumentException($"Certificate {certificate.ToString(verbose: true)} does not contain a private key", parameterName);
        }
    }
}
