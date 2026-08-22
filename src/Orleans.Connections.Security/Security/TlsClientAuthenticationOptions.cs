using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Orleans.Connections.Security
{
    public delegate X509Certificate? ClientCertificateSelectionCallback(object sender, string targetHost, X509CertificateCollection localCertificates, X509Certificate? remoteCertificate, string[] acceptableIssuers);

    public class TlsClientAuthenticationOptions
    {
        internal SslClientAuthenticationOptions Value { get; } = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                OrleansApplicationProtocol.Orleans1
            }
        };

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

        public SslProtocols EnabledSslProtocols
        {
            get => this.Value.EnabledSslProtocols;
            set => this.Value.EnabledSslProtocols = value;
        }

        public X509RevocationMode CertificateRevocationCheckMode
        {
            get => this.Value.CertificateRevocationCheckMode;
            set => this.Value.CertificateRevocationCheckMode = value;
        }

        public string? TargetHost
        {
            get => this.Value.TargetHost;
            set => this.Value.TargetHost = value;
        }

        public object SslClientAuthenticationOptions => this.Value;
    }
}
