using System.Net.Sockets;

namespace Orleans.Configuration.New
{
    public class LocalAddress
    {
        public LocalAddress()
        {
            PreferredFamily = AddressFamily.InterNetwork;
            Interface = null;
            Port = 0;
        }

        /// <summary>
        /// </summary>
        public AddressFamily PreferredFamily { get; set; }
        
        /// <summary>
        /// The Interface attribute specifies the name of the network interface to use to work out an IP address for this machine.
        /// </summary>
        public string Interface { get; set; }
        /// <summary>
        /// The Port attribute specifies the specific listen port for this client machine.
        /// If value is zero, then a random machine-assigned port number will be used.
        /// </summary>
        public int Port { get; set; }
    }
}