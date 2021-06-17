using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace FakeFx.Runtime
{
    /// <summary>
    /// Data class encapsulating the details of silo addresses.
    /// </summary>
    [Serializable]
    [DebuggerDisplay("SiloAddress {ToString()}")]
    [Orleans.GenerateSerializer]
    [Orleans.SuppressReferenceTracking]
    public sealed class SiloAddress : IEquatable<SiloAddress>, IComparable<SiloAddress>, IComparable
    {
        [NonSerialized]
        private int hashCode = 0;

        [NonSerialized]
        private bool hashCodeSet = false;

        [NonSerialized]
        private List<uint> uniformHashCache;

        [Orleans.Id(0)]
        public IPEndPoint Endpoint { get; private set; }

        [Orleans.Id(1)]
        public int Generation { get; private set; }

        [NonSerialized]
        private byte[] utf8;

        private const char SEPARATOR = '@';

        private static readonly long epoch = new DateTime(2010, 1, 1).Ticks;

        private static readonly Interner<Key, SiloAddress> siloAddressInterningCache = new(InternerConstants.SIZE_MEDIUM);

        /// <summary>Special constant value to indicate an empty SiloAddress.</summary>
        public static SiloAddress Zero { get; } = New(new IPEndPoint(0, 0), 0);

        /// <summary>
        /// Factory for creating new SiloAddresses with specified IP endpoint address and silo generation number.
        /// </summary>
        /// <param name="ep">IP endpoint address of the silo.</param>
        /// <param name="gen">Generation number of the silo.</param>
        /// <returns>SiloAddress object initialized with specified address and silo generation.</returns>
        public static SiloAddress New(IPEndPoint ep, int gen)
        {
            return siloAddressInterningCache.FindOrCreate(new Key(ep, gen), k => new SiloAddress(k.Endpoint, k.Generation));
        }

        public SiloAddress() { }

        private SiloAddress(IPEndPoint endpoint, int gen)
        {
            // Normalize endpoints
            if (endpoint.Address.IsIPv4MappedToIPv6)
            {
                endpoint = new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port);
            }

            Endpoint = endpoint;
            Generation = gen;
        }

        public bool IsClient { get { return Generation < 0; } }

        /// <summary> Allocate a new silo generation number. </summary>
        /// <returns>A new silo generation number.</returns>
        public static int AllocateNewGeneration()
        {
            long elapsed = (DateTime.UtcNow.Ticks - epoch) / TimeSpan.TicksPerSecond;
            return unchecked((int)elapsed); // Unchecked to truncate any bits beyond the lower 32
        }

        /// <summary>
        /// Return this SiloAddress in a standard string form, suitable for later use with the <c>FromParsableString</c> method.
        /// </summary>
        /// <returns>SiloAddress in a standard string format.</returns>
        public string ToParsableString()
        {
            // This must be the "inverse" of FromParsableString, and must be the same across all silos in a deployment.
            // Basically, this should never change unless the data content of SiloAddress changes
            if (utf8 != null)
            {
                return Encoding.UTF8.GetString(utf8);
            }

            return string.Format("{0}:{1}@{2}", Endpoint.Address, Endpoint.Port, Generation);
        }

        internal unsafe byte[] ToUtf8String()
        {
            if (utf8 is null)
            {
                var addr = Endpoint.Address.ToString();
                var size = Encoding.UTF8.GetByteCount(addr);

                // Allocate sufficient room for: address + ':' + port + '@' + generation
                Span<byte> buf = stackalloc byte[size + 1 + 11 + 1 + 11];
                fixed (char* src = addr)
                fixed (byte* dst = buf)
                {
                    size = Encoding.UTF8.GetBytes(src, addr.Length, dst, buf.Length);
                }

                buf[size++] = (byte)':';
                var success = Utf8Formatter.TryFormat(Endpoint.Port, buf.Slice(size), out var len);
                Debug.Assert(success);
                Debug.Assert(len > 0);
                Debug.Assert(len <= 11);
                size += len;

                buf[size++] = (byte)SEPARATOR;
                success = Utf8Formatter.TryFormat(Generation, buf.Slice(size), out len);
                Debug.Assert(success);
                Debug.Assert(len > 0);
                Debug.Assert(len <= 11);
                size += len;

                utf8 = buf.Slice(0, size).ToArray();
            }

            return utf8;
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
            // IPEndpoint is the host, then ':', then the port
            int lastColon = addr.LastIndexOf(':', atSign - 1);
            if (lastColon < 0)
            {
                throw new FormatException("Invalid string SiloAddress: " + addr);
            }

            var hostString = addr.Substring(0, lastColon);
            var host = IPAddress.Parse(hostString);
            var portString = addr.Substring(lastColon + 1, atSign - lastColon - 1);
            int port = int.Parse(portString, NumberStyles.None);
            var gen = int.Parse(addr.Substring(atSign + 1), NumberStyles.None);
            return New(new IPEndPoint(host, port), gen);
        }

        /// <summary>
        /// Create a new SiloAddress object by parsing string in a standard form returned from <c>ToParsableString</c> method.
        /// </summary>
        /// <param name="addr">String containing the SiloAddress info to be parsed.</param>
        /// <returns>New SiloAddress object created from the input data.</returns>
        public static SiloAddress FromUtf8String(ReadOnlySpan<byte> addr)
        {
            // This must be the "inverse" of ToParsableString, and must be the same across all silos in a deployment.
            // Basically, this should never change unless the data content of SiloAddress changes

            // First is the IPEndpoint; then '@'; then the generation
            var atSign = addr.LastIndexOf((byte)SEPARATOR);
            if (atSign < 0)
            {
                ThrowInvalidUtf8SiloAddress(addr);
            }

            // IPEndpoint is the host, then ':', then the port
            var endpointSlice = addr.Slice(0, atSign);
            int lastColon = endpointSlice.LastIndexOf((byte)':');
            if (lastColon < 0)
            {
                ThrowInvalidUtf8SiloAddress(addr);
            }

            var hostString = endpointSlice.Slice(0, lastColon).GetUtf8String();
            if (!IPAddress.TryParse(hostString, out var host))
            {
                ThrowInvalidUtf8SiloAddress(addr);
            }

            var portSlice = endpointSlice.Slice(lastColon + 1);
            if (!Utf8Parser.TryParse(portSlice, out int port, out var len) || len < portSlice.Length)
            {
                ThrowInvalidUtf8SiloAddress(addr);
            }

            var genSlice = addr.Slice(atSign + 1);
            if (!Utf8Parser.TryParse(genSlice, out int generation, out len) || len < genSlice.Length)
            {
                ThrowInvalidUtf8SiloAddress(addr);
            }

            return New(new IPEndPoint(host, port), generation);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidUtf8SiloAddress(ReadOnlySpan<byte> addr)
            => throw new FormatException("Invalid string SiloAddress: " + addr.GetUtf8String());

        private readonly struct Key : IEquatable<Key>
        {
            public readonly IPEndPoint Endpoint;
            public readonly int Generation;

            public Key(IPEndPoint endpoint, int generation)
            {
                Endpoint = endpoint;
                Generation = generation;
            }

            public override int GetHashCode() => Endpoint.GetHashCode() ^ Generation;

            public bool Equals(Key other) => Generation == other.Generation && Endpoint.Address.Equals(other.Endpoint.Address) && Endpoint.Port == other.Endpoint.Port;
        }

        /// <summary> Object.ToString method override. </summary>
        public override string ToString()
        {
            return string.Format(IsClient ? "C{0}:{1}" : "S{0}:{1}", Endpoint, Generation);
        }

        /// <summary>
        /// Return a long string representation of this SiloAddress.
        /// </summary>
        /// <remarks>
        /// Note: This string value is not comparable with the <c>FromParsableString</c> method -- use the <c>ToParsableString</c> method for that purpose.
        /// </remarks>
        /// <returns>String representation of this SiloAddress.</returns>
        public string ToLongString()
        {
            return ToString();
        }

        /// <summary>
        /// Return a long string representation of this SiloAddress, including it's consistent hash value.
        /// </summary>
        /// <remarks>
        /// Note: This string value is not comparable with the <c>FromParsableString</c> method -- use the <c>ToParsableString</c> method for that purpose.
        /// </remarks>
        /// <returns>String representation of this SiloAddress.</returns>
        public string ToStringWithHashCode()
        {
            return string.Format(IsClient ? "C{0}:{1}/x{2:X8}" : "S{0}:{1}/x{2:X8}", Endpoint, Generation, GetConsistentHashCode());
        }

        /// <summary> Object.Equals method override. </summary>
        public override bool Equals(object obj) => Equals(obj as SiloAddress);

        /// <summary> Object.GetHashCode method override. </summary>
        public override int GetHashCode() => Endpoint.GetHashCode() ^ Generation;

        /// <summary>Get a consistent hash value for this silo address.</summary>
        /// <returns>Consistent hash value for this silo address.</returns>
        public int GetConsistentHashCode()
        {
            if (hashCodeSet)
            {
                return hashCode;
            }

            string siloAddressInfoToHash = Endpoint.ToString() + Generation.ToString(CultureInfo.InvariantCulture);
            hashCode = CalculateIdHash(siloAddressInfoToHash);
            hashCodeSet = true;
            return hashCode;
        }

        // This is the same method as Utils.CalculateIdHash
        private static int CalculateIdHash(string text)
        {
            SHA256 sha = SHA256.Create(); // This is one implementation of the abstract class SHA1.
            int hash = 0;
            try
            {
                byte[] data = Encoding.Unicode.GetBytes(text);
                byte[] result = sha.ComputeHash(data);
                for (int i = 0; i < result.Length; i += 4)
                {
                    int tmp = (result[i] << 24) | (result[i + 1] << 16) | (result[i + 2] << 8) | (result[i + 3]);
                    hash = hash ^ tmp;
                }
            }
            finally
            {
                sha.Dispose();
            }
            return hash;
        }

        public List<uint> GetUniformHashCodes(int numHashes)
        {
            if (uniformHashCache != null)
            {
                return uniformHashCache;
            }

            uniformHashCache = GetUniformHashCodesImpl(numHashes);
            return uniformHashCache;
        }

        private List<uint> GetUniformHashCodesImpl(int numHashes)
        {
            Span<byte> bytes = stackalloc byte[16 + sizeof(int) + sizeof(int) + sizeof(int)]; // ip + port + generation + extraBit

            // Endpoint IP Address
            var address = Endpoint.Address;
            if (address.AddressFamily == AddressFamily.InterNetwork) // IPv4
            {
#pragma warning disable CS0618 // Type or member is obsolete
                BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(12), (int)address.Address);
#pragma warning restore CS0618
                bytes.Slice(0, 12).Clear();
            }
            else // IPv6
            {
                address.GetAddressBytes().CopyTo(bytes);
            }
            var offset = 16;
            // Port
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(offset), Endpoint.Port);
            offset += sizeof(int);
            // Generation
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(offset), Generation);
            offset += sizeof(int);

            var hashes = new List<uint>(numHashes);
            for (int extraBit = 0; extraBit < numHashes; extraBit++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(offset), extraBit);
                hashes.Add(JenkinsHash.ComputeHash(bytes));
            }
            return hashes;
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

        /// <summary> IEquatable.Equals method override. </summary>
        public bool Equals(SiloAddress other)
        {
            return other != null && Generation == other.Generation && Endpoint.Address.Equals(other.Endpoint.Address) && Endpoint.Port == other.Endpoint.Port;
        }

        internal bool IsSameLogicalSilo(SiloAddress other)
        {
            return other != null && this.Endpoint.Address.Equals(other.Endpoint.Address) && this.Endpoint.Port == other.Endpoint.Port;
        }

        public bool IsSuccessorOf(SiloAddress other)
        {
            return IsSameLogicalSilo(other) && this.Generation != 0 && other.Generation != 0 && this.Generation > other.Generation;
        }

        public bool IsPredecessorOf(SiloAddress other)
        {
            return IsSameLogicalSilo(other) && this.Generation != 0 && other.Generation != 0 && this.Generation < other.Generation;
        }

        // non-generic version of CompareTo is needed by some contexts. Just calls generic version.
        public int CompareTo(object obj)
        {
            return CompareTo((SiloAddress)obj);
        }

        public int CompareTo(SiloAddress other)
        {
            if (other == null)
            {
                return 1;
            }
            // Compare Generation first. It gives a cheap and fast way to compare, avoiding allocations 
            // and is also semantically meaningfull - older silos (with smaller Generation) will appear first in the comparison order.
            // Only if Generations are the same, go on to compare Ports and IPAddress (which is more expansive to compare).
            // Alternatively, we could compare ConsistentHashCode or UniformHashCode.
            int comp = Generation.CompareTo(other.Generation);
            if (comp != 0)
            {
                return comp;
            }

            comp = Endpoint.Port.CompareTo(other.Endpoint.Port);
            if (comp != 0)
            {
                return comp;
            }

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
            var f1 = one.AddressFamily;
            var f2 = two.AddressFamily;
            if (f1 != f2)
            {
                return f1 < f2 ? -1 : 1;
            }

            if (f1 == AddressFamily.InterNetwork)
            {
#pragma warning disable CS0618 // Type or member is obsolete
                return one.Address.CompareTo(two.Address);
#pragma warning restore CS0618
            }

            byte[] b1 = one.GetAddressBytes();
            byte[] b2 = two.GetAddressBytes();

            for (int i = 0; i < b1.Length; i++)
            {
                if (b1[i] < b2[i])
                {
                    return -1;
                }
                else if (b1[i] > b2[i])
                {
                    return 1;
                }
            }
            return 0;
        }
    }
}
