using Orleans.Runtime;
using System.Net;

namespace TestExtensions
{
    public static class SiloAddressUtils
    {
        private static readonly IPEndPoint localEndpoint = new IPEndPoint(IPAddress.Loopback, 0);

        public static SiloAddress NewLocalSiloAddress(int gen)
        {
            return SiloAddress.New(localEndpoint, gen);
        }
    }
}
