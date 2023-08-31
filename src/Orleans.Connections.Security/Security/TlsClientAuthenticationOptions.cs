using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Orleans.Connections.Security
{
    public delegate X509Certificate ClientCertificateSelectionCallback(object sender, string targetHost, X509CertificateCollection localCertificates, X509Certificate remoteCertificate, string[] acceptableIssuers);

    public class TlsClientAuthenticationOptions
    {
        internal SslClientAuthenticationOptions Value { get; } = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                OrleansApplicationProtocol.Orleans1
            }
        };

        public ClientCertificateSelectionCallback LocalCertificateSelectionCallback
        {
            get => Value.LocalCertificateSelectionCallback is null ? null : new ClientCertificateSelectionCallback(Value.LocalCertificateSelectionCallback);
            set => Value.LocalCertificateSelectionCallback = value is null ? null : new System.Net.Security.LocalCertificateSelectionCallback(value);
        }

        public X509CertificateCollection ClientCertificates
        {
            get => Value.ClientCertificates;
            set => Value.ClientCertificates = value;
        }

        public SslProtocols EnabledSslProtocols
        {
            get => Value.EnabledSslProtocols;
            set => Value.EnabledSslProtocols = value;
        }

        public X509RevocationMode CertificateRevocationCheckMode
        {
            get => Value.CertificateRevocationCheckMode;
            set => Value.CertificateRevocationCheckMode = value;
        }

        public string TargetHost
        {
            get => Value.TargetHost;
            set => Value.TargetHost = value;
        }

        public object SslClientAuthenticationOptions => Value;
    }
}
