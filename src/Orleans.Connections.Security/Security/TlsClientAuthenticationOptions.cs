using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Orleans.Connections.Security
{
    /// <summary>
    /// Selects a local certificate for client authentication.
    /// </summary>
    /// <param name="sender">The object requesting certificate selection.</param>
    /// <param name="targetHost">The name of the target host.</param>
    /// <param name="localCertificates">The available local certificates.</param>
    /// <param name="remoteCertificate">The remote certificate, if available.</param>
    /// <param name="acceptableIssuers">The certificate issuer names accepted by the remote endpoint.</param>
    /// <returns>The certificate to use for client authentication, or <see langword="null"/> to omit a client certificate.</returns>
    public delegate X509Certificate? ClientCertificateSelectionCallback(object sender, string targetHost, X509CertificateCollection localCertificates, X509Certificate? remoteCertificate, string[] acceptableIssuers);

    /// <summary>
    /// Configures client authentication for an Orleans TLS connection.
    /// </summary>
    public class TlsClientAuthenticationOptions
    {
        internal SslClientAuthenticationOptions Value { get; } = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                OrleansApplicationProtocol.Orleans1
            }
        };

        /// <summary>
        /// Gets or sets the callback which selects a local client certificate.
        /// </summary>
        public ClientCertificateSelectionCallback? LocalCertificateSelectionCallback
        {
            get => Value.LocalCertificateSelectionCallback is null ? null : new ClientCertificateSelectionCallback(Value.LocalCertificateSelectionCallback);
            set
            {
#if NET10_0_OR_GREATER
                Value.LocalCertificateSelectionCallback = value is null ? null : new System.Net.Security.LocalCertificateSelectionCallback(value);
#else
                Value.LocalCertificateSelectionCallback = value is null
                    ? null
                    : (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) =>
                        value(sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers)!;
#endif
            }
        }

        /// <summary>
        /// Gets or sets the certificates available for client authentication.
        /// </summary>
        public X509CertificateCollection? ClientCertificates
        {
            get => this.Value.ClientCertificates;
            set => this.Value.ClientCertificates = value;
        }

        /// <summary>
        /// Gets or sets the application protocols offered by the client during TLS application-layer protocol negotiation.
        /// </summary>
        public List<SslApplicationProtocol>? ApplicationProtocols
        {
            get => Value.ApplicationProtocols;
            set => Value.ApplicationProtocols = value;
        }

        /// <summary>
        /// Gets or sets the TLS protocol versions permitted for authentication.
        /// </summary>
        public SslProtocols EnabledSslProtocols
        {
            get => this.Value.EnabledSslProtocols;
            set => this.Value.EnabledSslProtocols = value;
        }

        /// <summary>
        /// Gets or sets the certificate revocation checking mode used during authentication.
        /// </summary>
        public X509RevocationMode CertificateRevocationCheckMode
        {
            get => this.Value.CertificateRevocationCheckMode;
            set => this.Value.CertificateRevocationCheckMode = value;
        }

        /// <summary>
        /// Gets or sets the target host name used for server certificate validation and Server Name Indication (SNI).
        /// </summary>
        public string? TargetHost
        {
            get => this.Value.TargetHost;
            set => this.Value.TargetHost = value;
        }

        /// <summary>
        /// Gets the underlying <see cref="System.Net.Security.SslClientAuthenticationOptions"/> instance.
        /// </summary>
        public object SslClientAuthenticationOptions => this.Value;
    }
}
