using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Orleans.Connections.Security
{
    public delegate X509Certificate ServerCertificateSelectionCallback(object sender, string hostName);

    public class TlsServerAuthenticationOptions
    {
        internal SslServerAuthenticationOptions Value { get; } = new SslServerAuthenticationOptions
        {
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                OrleansApplicationProtocol.Orleans1
            }
        };

        public X509Certificate ServerCertificate
        {
            get => Value.ServerCertificate;
            set => Value.ServerCertificate = value;
        }

        public ServerCertificateSelectionCallback ServerCertificateSelectionCallback
        {
            get => Value.ServerCertificateSelectionCallback is null ? null : new ServerCertificateSelectionCallback(Value.ServerCertificateSelectionCallback);
            set => Value.ServerCertificateSelectionCallback = value is null ? null : new System.Net.Security.ServerCertificateSelectionCallback(value);
        }

        public bool ClientCertificateRequired
        {
            get => Value.ClientCertificateRequired;
            set => Value.ClientCertificateRequired = value;
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

        public object SslServerAuthenticationOptions => this.Value;
    }
}
