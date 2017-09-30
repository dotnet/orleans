using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    public static class SiloAddressFactory
    {
        private static readonly TimeSpan internCacheCleanupInterval = TimeSpan.Zero;
        private static readonly Interner<SiloAddress, SiloAddress> siloAddressInterningCache;
        private static readonly IPEndPoint localEndpoint = new IPEndPoint(ClusterConfiguration.GetLocalIPAddress(), 0); // non loopback local ip.
        private const int INTERN_CACHE_INITIAL_SIZE = InternerConstants.SIZE_MEDIUM;
        private static readonly DateTime epoch = new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const char SEPARATOR = '@';

        /// <summary> Special constant value to indicate an empty SiloAddress. </summary>
        public static SiloAddress Zero { get; private set; }

        static SiloAddressFactory()
        {
            SiloAddressFactory.siloAddressInterningCache = new Interner<SiloAddress, SiloAddress>(SiloAddressFactory.INTERN_CACHE_INITIAL_SIZE, SiloAddressFactory.internCacheCleanupInterval);
            var sa = new SiloAddress(new IPEndPoint(0, 0), 0);
            SiloAddressFactory.Zero = SiloAddressFactory.siloAddressInterningCache.Intern(sa, sa);
        }

        /// <summary> Allocate a new silo generation number. </summary>
        /// <returns>A new silo generation number.</returns>
        public static int AllocateNewGeneration()
        {
            long elapsed = (DateTime.UtcNow.Ticks - epoch.Ticks) / TimeSpan.TicksPerSecond;
            return unchecked((int)elapsed); // Unchecked to truncate any bits beyond the lower 32
        }

        /// <summary>
        /// Factory for creating new SiloAddresses for silo on this machine with specified generation number.
        /// </summary>
        /// <param name="gen">Generation number of the silo.</param>
        /// <returns>SiloAddress object initialized with the non-loopback local IP address and the specified silo generation.</returns>
        public static SiloAddress NewLocalAddress(int gen)
        {
            return New(localEndpoint, gen);
        }

        /// <summary>
        /// Factory for creating new SiloAddresses with specified IP endpoint address and silo generation number.
        /// </summary>
        /// <param name="ep">IP endpoint address of the silo.</param>
        /// <param name="gen">Generation number of the silo.</param>
        /// <returns>SiloAddress object initialized with specified address and silo generation.</returns>
        public static SiloAddress New(IPEndPoint ep, int gen)
        {
            var sa = new SiloAddress(ep, gen);
            return siloAddressInterningCache.Intern(sa, sa);
        }

        /// <summary>
        /// Return this SiloAddress in a standard string form, suitable for later use with the <c>FromParsableString</c> method.
        /// </summary>
        /// <returns>SiloAddress in a standard string format.</returns>
        public static string ToParsableString(this SiloAddress @this)
        {
            // This must be the "inverse" of FromParsableString, and must be the same across all silos in a deployment.
            // Basically, this should never change unless the data content of SiloAddress changes

            return String.Format("{0}:{1}@{2}", @this.Endpoint.Address, @this.Endpoint.Port, @this.Generation);
        }

        /// <summary>
        /// Create a new SiloAddress object by parsing string in a standard form returned from <c>ToParsableString</c> method.
        /// </summary>
        /// <param name="addr">String containing the SiloAddress info to be parsed.</param>
        /// <returns>New SiloAddress object created from the input data.</returns>
        public static SiloAddress FromParsableString(string addr)
        {
            // This must be the "inverse" of ToParsableString, and must be the same across all silos in a deployment.
            // Basically, this should never change unless the data content of SiloAddress changes

            // First is the IPEndpoint; then '@'; then the generation
            int atSign = addr.LastIndexOf(SEPARATOR);
            if (atSign < 0)
            {
                throw new FormatException("Invalid string SiloAddress: " + addr);
            }
            var epString = addr.Substring(0, atSign);
            var genString = addr.Substring(atSign + 1);
            // IPEndpoint is the host, then ':', then the port
            int lastColon = epString.LastIndexOf(':');
            if (lastColon < 0) throw new FormatException("Invalid string SiloAddress: " + addr);

            var hostString = epString.Substring(0, lastColon);
            var portString = epString.Substring(lastColon + 1);
            var host = IPAddress.Parse(hostString);
            int port = Int32.Parse(portString);
            return SiloAddressFactory.New(new IPEndPoint(host, port), Int32.Parse(genString));
        }

        /// <summary>Get a consistent hash value for this silo address.</summary>
        /// <returns>Consistent hash value for this silo address.</returns>
        public static int GetConsistentHashCode(this SiloAddress @this)
        {
            if (@this.hashCodeSet) return @this.hashCode;

            // Note that Port cannot be used because Port==0 matches any non-zero Port value for .Equals
            string siloAddressInfoToHash = @this.Endpoint + @this.Generation.ToString(CultureInfo.InvariantCulture);
            @this.hashCode = Utils.CalculateIdHash(siloAddressInfoToHash);
            @this.hashCodeSet = true;
            return @this.hashCode;
        }

        /// <summary>
        /// Return a long string representation of this SiloAddress, including it's consistent hash value.
        /// </summary>
        /// <remarks>
        /// Note: This string value is not comparable with the <c>FromParsableString</c> method -- use the <c>ToParsableString</c> method for that purpose.
        /// </remarks>
        /// <returns>String representaiton of this SiloAddress.</returns>
        public static string ToStringWithHashCode(this SiloAddress @this)
        {
            return String.Format("{0}/x{1, 8:X8}", @this.ToString(), GetConsistentHashCode(@this));
        }

        public static List<uint> GetUniformHashCodes(this SiloAddress @this, int numHashes)
        {
            if (@this.uniformHashCache != null) return @this.uniformHashCache;

            var hashes = new List<uint>();
            for (int i = 0; i < numHashes; i++)
            {
                uint hash = GetUniformHashCode(@this, i);
                hashes.Add(hash);
            }
            @this.uniformHashCache = hashes;
            return @this.uniformHashCache;
        }

        private static uint GetUniformHashCode(SiloAddress address, int extraBit)
        {
            var writer = new BinaryTokenStreamWriter();
            writer.Write(address);
            writer.Write(extraBit);
            byte[] bytes = writer.ToByteArray();
            writer.ReleaseBuffers();
            return JenkinsHash.ComputeHash(bytes);
        }
    }
}