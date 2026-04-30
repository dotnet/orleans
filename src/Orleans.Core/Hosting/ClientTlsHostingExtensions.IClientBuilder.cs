using System;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Security;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    public static partial class ClientTlsHostingExtensions
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
        public static IClientBuilder UseTls(
            this IClientBuilder builder,
            StoreName storeName,
            string subject,
            bool allowInvalid,
            StoreLocation location,
            Action<TlsOptions> configureOptions)
        {
            ArgumentNullException.ThrowIfNull(configureOptions);

            return builder.UseTls(
                CertificateLoader.LoadFromStoreCert(subject, storeName.ToString(), location, allowInvalid, server: false),
                configureOptions);
        }

        /// <summary>
        /// Configures TLS.
        /// </summary>
        /// <param name="builder">The builder to configure.</param>
        /// <param name="certificate">The server certificate.</param>
        /// <param name="configureOptions">An Action to configure the <see cref="TlsOptions"/>.</param>
        /// <returns>The builder.</returns>
        public static IClientBuilder UseTls(
            this IClientBuilder builder,
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
        public static IClientBuilder UseTls(
            this IClientBuilder builder,
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
        public static IClientBuilder UseTls(
            this IClientBuilder builder,
            Action<TlsOptions> configureOptions)
        {
            ArgumentNullException.ThrowIfNull(configureOptions);

            builder.Configure<TlsOptions>(configureOptions);
            builder.Services.AddSingleton<IConfigurationValidator>(sp => new TlsOptionsValidator(sp.GetRequiredService<IOptions<TlsOptions>>().Value));
            builder.Services.AddSingleton<IMessageTransportConnectorMiddleware, TlsMessageTransportConnectorMiddleware>();
            return builder;
        }

        internal sealed class TlsOptionsValidator(TlsOptions options) : IConfigurationValidator
        {
            public void ValidateConfiguration()
            {
                if (options.LocalCertificate is null && options.ClientCertificateMode == RemoteCertificateMode.RequireCertificate)
                {
                    throw new OrleansConfigurationException("No certificate specified");
                }

                if (options.LocalCertificate is X509Certificate2 certificate && !certificate.HasPrivateKey)
                {
                    throw new OrleansConfigurationException($"Certificate {certificate.ToString(verbose: true)} does not contain a private key");
                }
            }
        }
    }
}
