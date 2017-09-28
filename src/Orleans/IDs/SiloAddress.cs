using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;

namespace Orleans.Runtime
{
    /// <summary>
    /// Data class encapsulating the details of silo addresses.
    /// </summary>
    [Serializable]
    [DebuggerDisplay("SiloAddress {ToString()}")]
    public class SiloAddress : IEquatable<SiloAddress>, IComparable<SiloAddress>, IComparable
    {
        internal static readonly int SizeBytes = 24; // 16 for the address, 4 for the port, 4 for the generation

        // TODO theses values are set by extensions method in SiloAddressFactory. We should find a 
        // way to cachec this value without this ugly hack
        internal int hashCode = 0;
        internal bool hashCodeSet = false;
        [NonSerialized] internal List<uint> uniformHashCache;
        // TODO END

        public IPEndPoint Endpoint { get; private set; }
        public int Generation { get; private set; }

        internal SiloAddress(IPEndPoint endpoint, int gen)
        {
            Endpoint = endpoint;
            Generation = gen;
        }

        public bool IsClient { get { return Generation < 0; } }

        /// <summary> Object.ToString method override. </summary>
        public override string ToString()
        {
            return String.Format("{0}{1}:{2}", (IsClient ? "C" : "S"), Endpoint, Generation);
        }

        /// <summary>
        /// Return a long string representation of this SiloAddress.
        /// </summary>
        /// <remarks>
        /// Note: This string value is not comparable with the <c>FromParsableString</c> method -- use the <c>ToParsableString</c> method for that purpose.
        /// </remarks>
        /// <returns>String representaiton of this SiloAddress.</returns>
        public string ToLongString()
        {
            return ToString();
        }

        /// <summary> Object.Equals method override. </summary>
        public override bool Equals(object obj)
        {
            return Equals(obj as SiloAddress);
        }

        /// <summary> Object.GetHashCode method override. </summary>
        public override int GetHashCode()
        {
            // Note that Port cannot be used because Port==0 matches any non-zero Port value for .Equals
            return Endpoint.GetHashCode() ^ Generation.GetHashCode();
        }

        /// <summary>
        /// Two silo addresses match if they are equal or if one generation or the other is 0
        /// </summary>
        /// <param name="other"> The other SiloAddress to compare this one with. </param>
        /// <returns> Returns <c>true</c> if the two SiloAddresses are considered to match -- if they are equal or if one generation or the other is 0. </returns>
        internal bool Matches(SiloAddress other)
        {
            return other != null && Endpoint.Address.Equals(other.Endpoint.Address) && (Endpoint.Port == other.Endpoint.Port) &&
                ((Generation == other.Generation) || (Generation == 0) || (other.Generation == 0));
        }

        #region IEquatable<SiloAddress> Members

        /// <summary> IEquatable.Equals method override. </summary>
        public bool Equals(SiloAddress other)
        {
            return other != null && Endpoint.Address.Equals(other.Endpoint.Address) && (Endpoint.Port == other.Endpoint.Port) &&
                ((Generation == other.Generation));
        }

        #endregion


        // non-generic version of CompareTo is needed by some contexts. Just calls generic version.
        public int CompareTo(object obj)
        {
            return CompareTo((SiloAddress)obj);
        }


        public int CompareTo(SiloAddress other)
        {
            if (other == null) return 1;
            // Compare Generation first. It gives a cheap and fast way to compare, avoiding allocations 
            // and is also semantically meaningfull - older silos (with smaller Generation) will appear first in the comparison order.
            // Only if Generations are the same, go on to compare Ports and IPAddress (which is more expansive to compare).
            // Alternatively, we could compare ConsistentHashCode or UniformHashCode.
            int comp = Generation.CompareTo(other.Generation);
            if (comp != 0) return comp;

            comp = Endpoint.Port.CompareTo(other.Endpoint.Port);
            if (comp != 0) return comp;

            return CompareIpAddresses(Endpoint.Address, other.Endpoint.Address);
        }

        // The comparions code is taken from: http://www.codeproject.com/Articles/26550/Extending-the-IPAddress-object-to-allow-relative-c
        // Also note that this comparison does not handle semantic equivalence  of IPv4 and IPv6 addresses.
        // In particular, 127.0.0.1 and::1 are semanticaly the same, but not syntacticaly.
        // For more information refer to: http://stackoverflow.com/questions/16618810/compare-ipv4-addresses-in-ipv6-notation 
        // and http://stackoverflow.com/questions/22187690/ip-address-class-getaddressbytes-method-putting-octets-in-odd-indices-of-the-byt
        // and dual stack sockets, described at https://msdn.microsoft.com/en-us/library/system.net.ipaddress.maptoipv6(v=vs.110).aspx
        private static int CompareIpAddresses(IPAddress one, IPAddress two)
        {
            int returnVal = 0;
            if (one.AddressFamily == two.AddressFamily)
            {
                byte[] b1 = one.GetAddressBytes();
                byte[] b2 = two.GetAddressBytes();

                for (int i = 0; i < b1.Length; i++)
                {
                    if (b1[i] < b2[i])
                    {
                        returnVal = -1;
                        break;
                    }
                    else if (b1[i] > b2[i])
                    {
                        returnVal = 1;
                        break;
                    }
                }
            }
            else
            {
                returnVal = one.AddressFamily.CompareTo(two.AddressFamily);
            }
            return returnVal;
        }

      }
}
