using System;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Connections.Security
{
    internal class TlsConnectionFeature : ITlsConnectionFeature, ITlsApplicationProtocolFeature, ITlsHandshakeFeature
    {
        public X509Certificate2 LocalCertificate { get; set; }

        public X509Certificate2 RemoteCertificate { get; set; }

        public ReadOnlyMemory<byte> ApplicationProtocol { get; set; }

        public SslProtocols Protocol { get; set; }

        public CipherAlgorithmType CipherAlgorithm { get; set; }

        public int CipherStrength { get; set; }

        public HashAlgorithmType HashAlgorithm { get; set; }

        public int HashStrength { get; set; }

        public ExchangeAlgorithmType KeyExchangeAlgorithm { get; set; }

        public int KeyExchangeStrength { get; set; }

        public Task<X509Certificate2> GetRemoteCertificateAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(RemoteCertificate);
        }
    }
}
