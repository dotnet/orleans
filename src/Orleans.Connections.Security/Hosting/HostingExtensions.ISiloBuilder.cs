using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
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
            var options = CreateAndValidateOptions(configureOptions);
            return builder
                .UseSiloTls(options)
                .UseGatewayTls(options);
        }

        /// <summary>
        /// Configures TLS for connections between silos.
        /// </summary>
        /// <param name="builder">The builder to configure.</param>
        /// <param name="configureOptions">An action to configure the <see cref="TlsOptions"/>.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder UseSiloTls(
            this ISiloBuilder builder,
            Action<TlsOptions> configureOptions)
        {
            return builder.UseSiloTls(CreateAndValidateOptions(configureOptions));
        }

        /// <summary>
        /// Configures TLS for gateway connections from clients.
        /// </summary>
        /// <param name="builder">The builder to configure.</param>
        /// <param name="configureOptions">An action to configure the <see cref="TlsOptions"/>.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder UseGatewayTls(
            this ISiloBuilder builder,
            Action<TlsOptions> configureOptions)
        {
            return builder.UseGatewayTls(CreateAndValidateOptions(configureOptions));
        }

        private static ISiloBuilder UseSiloTls(this ISiloBuilder builder, TlsOptions options)
        {
            if (builder.Services.Any(descriptor =>
                descriptor.ServiceType == typeof(SiloConnectionAuthenticationRegistration)
                || descriptor.ServiceType == typeof(SiloTlsRegistrationMarker)))
            {
                throw new InvalidOperationException("Silo TLS or connection authentication has already been configured.");
            }

            builder.Services.AddSingleton<SiloTlsRegistrationMarker>();

            return builder.Configure<SiloConnectionOptions>(connectionOptions =>
            {
                connectionOptions.ConfigureSiloInboundConnection(connectionBuilder =>
                {
                    connectionBuilder.UseServerTls(options);
                });

                connectionOptions.ConfigureSiloOutboundConnection(connectionBuilder =>
                {
                    connectionBuilder.UseClientTls(options);
                });
            });
        }

        private static ISiloBuilder UseGatewayTls(this ISiloBuilder builder, TlsOptions options)
        {
            if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(GatewayTlsRegistrationMarker)))
            {
                throw new InvalidOperationException("Gateway TLS has already been configured.");
            }

            if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(GatewayConnectionAuthenticationRegistration)))
            {
                throw new InvalidOperationException("Gateway TLS or client connection authentication has already been configured.");
            }

            builder.Services.AddSingleton<GatewayTlsRegistrationMarker>();

            return builder.Configure<SiloConnectionOptions>(connectionOptions =>
            {
                connectionOptions.ConfigureGatewayInboundConnection(connectionBuilder =>
                {
                    connectionBuilder.UseServerTls(options);
                });
            });
        }

        private static TlsOptions CreateAndValidateOptions(Action<TlsOptions> configureOptions)
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

            return options;
        }
    }
}
