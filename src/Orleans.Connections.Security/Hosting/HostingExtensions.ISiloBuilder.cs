using System;
using System.Security.Cryptography.X509Certificates;
using Orleans.Configuration;
using Orleans.Connections.Security;

namespace Orleans.Hosting
{
    public static partial class OrleansConnectionSecurityHostingExtensions
    {
        /// <summary>
        /// Configures TLS.
        /// </summary>
        /// <param name="builder">The builder to configure.</param>
        /// <param name="storeName">The certificate store to load the certificate from.</param>
        /// <param name="subject">The subject name for the certificate to load.</param>
        /// <param name="allowInvalid">Indicates if invalid certificates should be considered, such as self-signed certificates.</param>
        /// <param name="location">The store location to load the certificate from.</param>
        /// <param name="configureOptions">An Action to configure the <see cref="TlsOptions"/>.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder UseTls(
            this ISiloBuilder builder,
            StoreName storeName,
            string subject,
            bool allowInvalid,
            StoreLocation location,
            Action<TlsOptions> configureOptions)
        {
            if (configureOptions is null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            return builder.UseTls(
                CertificateLoader.LoadFromStoreCert(subject, storeName.ToString(), location, allowInvalid, server: true),
                configureOptions);
        }

        /// <summary>
        /// Configures TLS.
        /// </summary>
        /// <param name="builder">The builder to configure.</param>
        /// <param name="certificate">The server certificate.</param>
        /// <param name="configureOptions">An Action to configure the <see cref="TlsOptions"/>.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder UseTls(
            this ISiloBuilder builder,
            X509Certificate2 certificate,
            Action<TlsOptions> configureOptions)
        {
            if (certificate is null)
            {
                throw new ArgumentNullException(nameof(certificate));
            }

            if (configureOptions is null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            if (!certificate.HasPrivateKey)
            {
                TlsConnectionBuilderExtensions.ThrowNoPrivateKey(certificate, nameof(certificate));
            }

            return builder.UseTls(options =>
            {
                options.LocalCertificate = certificate;
                configureOptions(options);
            });
        }

        /// <summary>
        /// Configures TLS.
        /// </summary>
        /// <param name="builder">The builder to configure.</param>
        /// <param name="certificate">The server certificate.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder UseTls(
            this ISiloBuilder builder,
            X509Certificate2 certificate)
        {
            if (certificate is null)
            {
                throw new ArgumentNullException(nameof(certificate));
            }

            if (!certificate.HasPrivateKey)
            {
                TlsConnectionBuilderExtensions.ThrowNoPrivateKey(certificate, nameof(certificate));
            }

            return builder.UseTls(options =>
            {
                options.LocalCertificate = certificate;
            });
        }

        /// <summary>
        /// Configures TLS.
        /// </summary>
        /// <param name="builder">The builder to configure.</param>
        /// <param name="configureOptions">An Action to configure the <see cref="TlsOptions"/>.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder UseTls(
            this ISiloBuilder builder,
            Action<TlsOptions> configureOptions)
        {
            if (configureOptions is null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            var options = new TlsOptions();
            configureOptions(options);
            if (options.LocalCertificate is null && options.LocalServerCertificateSelector is null)
            {
                throw new InvalidOperationException("No certificate specified");
            }

            if (options.LocalCertificate is X509Certificate2 certificate && !certificate.HasPrivateKey)
            {
                TlsConnectionBuilderExtensions.ThrowNoPrivateKey(certificate, $"{nameof(TlsOptions)}.{nameof(TlsOptions.LocalCertificate)}");
            }

            return builder.Configure<SiloConnectionOptions>(connectionOptions =>
            {
                connectionOptions.ConfigureSiloInboundConnection(connectionBuilder =>
                {
                    connectionBuilder.UseServerTls(options);
                });

                connectionOptions.ConfigureGatewayInboundConnection(connectionBuilder =>
                {
                    connectionBuilder.UseServerTls(options);
                });

                connectionOptions.ConfigureSiloOutboundConnection(connectionBuilder =>
                {
                    connectionBuilder.UseClientTls(options);
                });
            });
        }
    }
}
