using System.Net.Security;

namespace Orleans.Connections.Security
{
    internal static class OrleansApplicationProtocol
    {
        public static readonly SslApplicationProtocol Orleans1 = new SslApplicationProtocol("Orleans1");
    }
}
