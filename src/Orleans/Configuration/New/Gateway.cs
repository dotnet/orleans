using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.Configuration.New
{
    public class Gateway
    {
        public string Address { get; set; }
        public int? Port { get; set; }
        public string Subnet { get; set; }
        public string PreferredFamily { get; set; }

        internal async Task<IPEndPoint> ParseIPEndPoint()
        {
            if (string.IsNullOrEmpty(Address)) throw new FormatException("Missing Address attribute");
            if (Port == null) throw new FormatException("Missing Port attribute");
            
            var family = AddressFamily.InterNetwork;
            byte[] subnet = null;
            if (!string.IsNullOrEmpty(Subnet))
            {
                subnet = ConfigUtilities.ParseSubnet(Subnet, "Invalid subnet");
            }
            if (!string.IsNullOrEmpty(PreferredFamily))
            {
                family = ConfigUtilities.ParseEnum<AddressFamily>(PreferredFamily, "Invalid preferred addressing family");
            }
            IPAddress addr = await ClusterConfiguration.ResolveIPAddress(Address, subnet, family);
            return new IPEndPoint(addr, Port.Value);
        }
    }
}
