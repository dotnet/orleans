using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Orleans.Connections.Security
{
    /// <summary>
    /// Selects a server certificate using the host name supplied by the client.
    /// </summary>
    /// <param name="sender">The object requesting certificate selection.</param>
    /// <param name="hostName">The host name supplied through Server Name Indication (SNI), if available.</param>
    /// <returns>The certificate to use for server authentication.</returns>
    public delegate X509Certificate ServerCertificateSelectionCallback(object sender, string? hostName);

    /// <summary>
    /// Configures server authentication for an Orleans TLS connection.
    /// </summary>
    public class TlsServerAuthenticationOptions
    {
        internal SslServerAuthenticationOptions Value { get; } = new SslServerAuthenticationOptions
        {
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                OrleansApplicationProtocol.Orleans1
            }
        };

        /// <summary>
        /// Gets or sets the certificate used for server authentication.
        /// </summary>
        public X509Certificate? ServerCertificate
        {
            get => Value.ServerCertificate;
            set => Value.ServerCertificate = value;
        }

        /// <summary>
        /// Gets or sets the callback which selects a server certificate for each connection.
        /// </summary>
        public ServerCertificateSelectionCallback? ServerCertificateSelectionCallback
        {
            get => Value.ServerCertificateSelectionCallback is null ? null : new ServerCertificateSelectionCallback(Value.ServerCertificateSelectionCallback);
            set => Value.ServerCertificateSelectionCallback = value is null ? null : new System.Net.Security.ServerCertificateSelectionCallback(value);
        }

        /// <summary>
        /// Gets or sets a value indicating whether TLS authentication requests a client certificate.
        /// </summary>
        public bool ClientCertificateRequired
        {
            get => Value.ClientCertificateRequired;
            set => Value.ClientCertificateRequired = value;
        }

        /// <summary>
        /// Gets or sets the application protocols accepted by the server during TLS application-layer protocol negotiation.
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
            get => Value.EnabledSslProtocols;
            set => Value.EnabledSslProtocols = value;
        }

        /// <summary>
        /// Gets or sets the certificate revocation checking mode used during authentication.
        /// </summary>
        public X509RevocationMode CertificateRevocationCheckMode
        {
            get => Value.CertificateRevocationCheckMode;
            set => Value.CertificateRevocationCheckMode = value;
        }

        /// <summary>
        /// Gets the underlying <see cref="System.Net.Security.SslServerAuthenticationOptions"/> instance.
        /// </summary>
        public object SslServerAuthenticationOptions => this.Value;
    }
}
