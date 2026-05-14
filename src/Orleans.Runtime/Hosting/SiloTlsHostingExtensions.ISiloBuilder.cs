using System;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Security;
using Orleans.Runtime.Messaging;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for configuring a silo with TLS.
    /// </summary>
    public static partial class SiloTlsHostingExtensions
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
            ArgumentNullException.ThrowIfNull(configureOptions);

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
            ArgumentNullException.ThrowIfNull(certificate);

            ArgumentNullException.ThrowIfNull(configureOptions);

            if (!certificate.HasPrivateKey)
            {
                throw new ArgumentException($"Certificate {certificate.ToString(verbose: true)} does not contain a private key", nameof(certificate));
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
            ArgumentNullException.ThrowIfNull(certificate);

            if (!certificate.HasPrivateKey)
            {
                throw new ArgumentException($"Certificate {certificate.ToString(verbose: true)} does not contain a private key", nameof(certificate));
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
            ArgumentNullException.ThrowIfNull(configureOptions);

            var services = builder.Services;

            // Configure TLS options for each of the connection types.
            services.Configure<TlsOptions>(configureOptions);
            builder.Services.AddSingleton<IConfigurationValidator>(sp => new ClientTlsHostingExtensions.TlsOptionsValidator(sp.GetRequiredService<IOptions<TlsOptions>>().Value));
            services.AddOptions<TlsOptions>(SiloConnectionListener.DefaultListenerName).Configure(configureOptions);
            builder.Services.AddSingleton<IConfigurationValidator>(
                sp => new ClientTlsHostingExtensions.TlsOptionsValidator(
                    sp.GetRequiredService<IOptionsMonitor<TlsOptions>>().Get(SiloConnectionListener.DefaultListenerName)));
            services.AddOptions<TlsOptions>(GatewayConnectionListener.DefaultListenerName).Configure(configureOptions);
            builder.Services.AddSingleton<IConfigurationValidator>(
                sp => new ClientTlsHostingExtensions.TlsOptionsValidator(
                    sp.GetRequiredService<IOptionsMonitor<TlsOptions>>().Get(GatewayConnectionListener.DefaultListenerName)));

            builder.Services.AddSingleton<IMessageTransportConnectorMiddleware, TlsMessageTransportConnectorMiddleware>();
            builder.Services.AddSingleton<IMessageTransportListenerMiddleware, TlsMessageTransportListenerMiddleware>();
            return builder;
        }
    }
}
