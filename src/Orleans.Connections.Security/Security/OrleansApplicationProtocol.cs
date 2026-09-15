using System.Net.Security;

namespace Orleans.Connections.Security
{
    internal static class OrleansApplicationProtocol
    {
        public static readonly SslApplicationProtocol Orleans1 = new SslApplicationProtocol("Orleans1");
        public static readonly SslApplicationProtocol Orleans1TokenAuth2 = new SslApplicationProtocol(SiloConnectionAuthenticationProtocol.Version2);
    }
}
