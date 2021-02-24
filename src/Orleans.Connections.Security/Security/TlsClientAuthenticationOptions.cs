using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Orleans.Connections.Security
{
    public class TlsClientAuthenticationOptions
    {
        internal SslClientAuthenticationOptions Value { get; } = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                OrleansApplicationProtocol.Orleans1
            }
        };

        public X509CertificateCollection ClientCertificates
        {
            get => this.Value.ClientCertificates;
            set => this.Value.ClientCertificates = value;
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

        public string TargetHost
        {
            get => this.Value.TargetHost;
            set => this.Value.TargetHost = value;
        }

        public object SslClientAuthenticationOptions => this.Value;
    }
}
