using System.Net;
using Orleans.Runtime;

namespace Tester.Cassandra.Clustering;

public static class SiloAddressUtils
{
    private static readonly IPEndPoint s_localEndpoint = new(IPAddress.Loopback, 0);

    public static SiloAddress NewLocalSiloAddress(int gen)
    {
        return SiloAddress.New(s_localEndpoint, gen);
    }
}