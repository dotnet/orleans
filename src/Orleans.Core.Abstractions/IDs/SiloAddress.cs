using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable
namespace Orleans.Runtime
{
    /// <summary>
    /// Data class encapsulating the details of silo addresses.
    /// </summary>
    [Serializable, Immutable]
    [JsonConverter(typeof(SiloAddressConverter))]
    [DebuggerDisplay("SiloAddress {ToString()}")]
    [SuppressReferenceTracking]
    public sealed class SiloAddress : IEquatable<SiloAddress>, IComparable<SiloAddress>, ISpanFormattable
    {
        [NonSerialized]
        private int hashCode;

        [NonSerialized]
        private bool hashCodeSet;

        [NonSerialized]
        private uint[]? uniformHashCache;

        /// <summary>
        /// Gets the endpoint.
        /// </summary>
        [Id(0)]
        public IPEndPoint Endpoint { get; }

        /// <summary>
        /// Gets the generation.
        /// </summary>
        [Id(1)]
        public int Generation { get; }

        [NonSerialized]
        private byte[]? utf8;

        private const char SEPARATOR = '@';

        private static readonly long epoch = new DateTime(2022, 1, 1).Ticks;

        private static readonly Interner<(IPAddress Address, int Port, int Generation), SiloAddress> siloAddressInterningCache = new(InternerConstants.SIZE_MEDIUM);

        /// <summary>Gets the special constant value which indicate an empty <see cref="SiloAddress"/>.</summary>
        public static SiloAddress Zero { get; } = New(new IPAddress(0), 0, 0);

        /// <summary>
        /// Factory for creating new SiloAddresses with specified IP endpoint address and silo generation number.
        /// </summary>
        /// <param name="ep">IP endpoint address of the silo.</param>
        /// <param name="gen">Generation number of the silo.</param>
        /// <returns>SiloAddress object initialized with specified address and silo generation.</returns>
        public static SiloAddress New(IPEndPoint ep, int gen)
        {
            return siloAddressInterningCache.FindOrCreate((ep.Address, ep.Port, gen),
                // Normalize endpoints
                (k, ep) => k.Address.IsIPv4MappedToIPv6 ? New(k.Address.MapToIPv4(), k.Port, k.Generation) : new(ep, k.Generation), ep);
        }

        /// <summary>
        /// Factory for creating new SiloAddresses with specified IP endpoint address and silo generation number.
        /// </summary>
        /// <param name="address">IP address of the silo.</param>
        /// <param name="port">Port number</param>
        /// <param name="generation">Generation number of the silo.</param>
        /// <returns>SiloAddress object initialized with specified address and silo generation.</returns>
        public static SiloAddress New(IPAddress address, int port, int generation)
        {
            return siloAddressInterningCache.FindOrCreate((address, port, generation),
                // Normalize endpoints
                k => k.Address.IsIPv4MappedToIPv6 ? New(k.Address.MapToIPv4(), k.Port, k.Generation) : new(new(k.Address, k.Port), k.Generation));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SiloAddress"/> class.
        /// </summary>
        /// <param name="endpoint">The endpoint.</param>
        /// <param name="generation">The generation.</param>
        private SiloAddress(IPEndPoint endpoint, int generation)
        {
            Endpoint = endpoint;
            Generation = generation;
        }

        /// <summary>
        /// Gets a value indicating whether this instance represents a client (versus a server).
        /// </summary>
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
            if (utf8 != null) return Encoding.UTF8.GetString(utf8);
            return $"{new SpanFormattableIPAddress(Endpoint.Address)}:{Endpoint.Port}@{Generation}";
        }

        /// <summary>
        /// Returns a UTF8-encoded representation of this instance as a byte array.
        /// </summary>
        /// <returns>A UTF8-encoded representation of this instance as a byte array.</returns>
        internal byte[] ToUtf8String()
        {
            if (utf8 is null)
            {
                Span<char> chars = stackalloc char[45];
                var addr = Endpoint.Address.TryFormat(chars, out var len) ? chars[..len] : Endpoint.Address.ToString().AsSpan();
                var size = Encoding.UTF8.GetByteCount(addr);

                // Allocate sufficient room for: address + ':' + port + '@' + generation
                Span<byte> buf = stackalloc byte[size + 1 + 11 + 1 + 11];
                size = Encoding.UTF8.GetBytes(addr, buf);

                buf[size++] = (byte)':';
                var success = Utf8Formatter.TryFormat(Endpoint.Port, buf.Slice(size), out len);
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
            // IPEndpoint is the host, then ':', then the port
            int lastColon = addr.LastIndexOf(':', atSign - 1);
            if (atSign < 0 || lastColon < 0) throw new FormatException("Invalid string SiloAddress: " + addr);

            var host = IPAddress.Parse(addr.AsSpan(0, lastColon));
            int port = int.Parse(addr.AsSpan(lastColon + 1, atSign - lastColon - 1), NumberStyles.None);
            var gen = int.Parse(addr.AsSpan(atSign + 1), NumberStyles.None);
            return New(host, port, gen);
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
            if (atSign < 0) ThrowInvalidUtf8SiloAddress(addr);

            // IPEndpoint is the host, then ':', then the port
            var endpointSlice = addr.Slice(0, atSign);
            int lastColon = endpointSlice.LastIndexOf((byte)':');
            if (lastColon < 0) ThrowInvalidUtf8SiloAddress(addr);

            var ipSlice = endpointSlice[..lastColon];
            Span<char> buf = stackalloc char[45];
            var hostString = Encoding.UTF8.GetCharCount(ipSlice) is int len && len <= buf.Length
                ? buf[..Encoding.UTF8.GetChars(ipSlice, buf)]
                : Encoding.UTF8.GetString(ipSlice).AsSpan();
            if (!IPAddress.TryParse(hostString, out var host))
                ThrowInvalidUtf8SiloAddress(addr);

            var portSlice = endpointSlice.Slice(lastColon + 1);
            if (!Utf8Parser.TryParse(portSlice, out int port, out len) || len < portSlice.Length)
                ThrowInvalidUtf8SiloAddress(addr);

            var genSlice = addr.Slice(atSign + 1);
            if (!Utf8Parser.TryParse(genSlice, out int generation, out len) || len < genSlice.Length)
                ThrowInvalidUtf8SiloAddress(addr);

            return New(host, port, generation);
        }

        [DoesNotReturn]
        private static void ThrowInvalidUtf8SiloAddress(ReadOnlySpan<byte> addr)
            => throw new FormatException("Invalid string SiloAddress: " + Encoding.UTF8.GetString(addr));

        /// <summary>
        /// Return a long string representation of this SiloAddress.
        /// </summary>
        /// <remarks>
        /// Note: This string value is not comparable with the <see cref="FromParsableString"/> method -- use the <see cref="ToParsableString"/> method for that purpose.
        /// </remarks>
        /// <returns>String representation of this SiloAddress.</returns>
        public override string ToString() => $"{this}";

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            if (!destination.TryWrite($"{(IsClient ? 'C' : 'S')}{new SpanFormattableIPEndPoint(Endpoint)}:{Generation}", out charsWritten))
                return false;

            if (format.Length == 1 && format[0] == 'H')
            {
                if (!destination[charsWritten..].TryWrite($"/x{GetConsistentHashCode():X8}", out var len))
                    return false;

                charsWritten += len;
            }

            return true;
        }

        /// <summary>
        /// Return a long string representation of this SiloAddress, including it's consistent hash value.
        /// </summary>
        /// <remarks>
        /// Note: This string value is not comparable with the <c>FromParsableString</c> method -- use the <c>ToParsableString</c> method for that purpose.
        /// </remarks>
        /// <returns>String representation of this SiloAddress.</returns>
        public string ToStringWithHashCode() => $"{this:H}";

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as SiloAddress);

        /// <inheritdoc />
        public override int GetHashCode() => Endpoint.GetHashCode() ^ Generation;

        /// <summary>Returns a consistent hash value for this silo address.</summary>
        /// <returns>Consistent hash value for this silo address.</returns>
        public int GetConsistentHashCode() => hashCodeSet ? hashCode : CalculateConsistentHashCode();

        private int CalculateConsistentHashCode()
        {
            Unsafe.SkipInit(out (long, long, long) tmp); // avoid stackalloc overhead by using a fixed size buffer
            var buf = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref tmp, 1))[..24];

            Endpoint.Address.TryWriteBytes(buf, out var len);
            Debug.Assert(len is 4 or 16);

            BinaryPrimitives.WriteInt32LittleEndian(buf[16..], Endpoint.Port);
            BinaryPrimitives.WriteInt32LittleEndian(buf[20..], Generation);

            hashCode = (int)StableHash.ComputeHash(buf);
            hashCodeSet = true;
            return hashCode;
        }

        internal void InternalSetConsistentHashCode(int hashCode)
        {
            this.hashCode = hashCode;
            this.hashCodeSet = true;
        }

        /// <summary>
        /// Returns a collection of uniform hash codes variants for this instance.
        /// </summary>
        /// <param name="numHashes">The number of hash codes to return.</param>
        /// <returns>A collection of uniform hash codes variants for this instance.</returns>
        public uint[] GetUniformHashCodes(int numHashes) => uniformHashCache ??= GetUniformHashCodesImpl(numHashes);

        private uint[] GetUniformHashCodesImpl(int numHashes)
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
                address.TryWriteBytes(bytes, out var len);
                Debug.Assert(len == 16);
            }
            var offset = 16;
            // Port
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(offset), Endpoint.Port);
            offset += sizeof(int);
            // Generation
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(offset), Generation);
            offset += sizeof(int);

            var hashes = new uint[numHashes];
            for (int extraBit = 0; extraBit < numHashes; extraBit++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(offset), extraBit);
                hashes[extraBit] = StableHash.ComputeHash(bytes);
            }
            return hashes;
        }

        /// <summary>
        /// Two silo addresses match if they are equal or if one generation or the other is 0.
        /// </summary>
        /// <param name="other"> The other SiloAddress to compare this one with. </param>
        /// <returns>Returns <c>true</c> if the two SiloAddresses are considered to match -- if they are equal or if one generation or the other is 0. </returns>
        internal bool Matches([NotNullWhen(true)] SiloAddress? other)
        {
            return other != null && Endpoint.Address.Equals(other.Endpoint.Address) && Endpoint.Port == other.Endpoint.Port &&
                (Generation == other.Generation || Generation == 0 || other.Generation == 0);
        }

        /// <inheritdoc/>
        public bool Equals([NotNullWhen(true)] SiloAddress? other)
            => other != null && Generation == other.Generation && Endpoint.Address.Equals(other.Endpoint.Address) && Endpoint.Port == other.Endpoint.Port;

        /// <summary>
        /// Returns <see langword="true"/> if the provided value represents the same logical server as this value, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="other">
        /// The other instance.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the provided value represents the same logical server as this value, otherwise <see langword="false"/>.
        /// </returns>
        internal bool IsSameLogicalSilo([NotNullWhen(true)] SiloAddress? other)
            => other != null && Endpoint.Address.Equals(other.Endpoint.Address) && Endpoint.Port == other.Endpoint.Port;

        /// <summary>
        /// Returns <see langword="true"/> if the provided value represents the same logical server as this value and is a successor to this server, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="other">
        /// The other instance.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the provided value represents the same logical server as this value and is a successor to this server, otherwise <see langword="false"/>.
        /// </returns>
        public bool IsSuccessorOf(SiloAddress other) => IsSameLogicalSilo(other) && other.Generation > 0 && Generation > other.Generation;

        /// <summary>
        /// Returns <see langword="true"/> if the provided value represents the same logical server as this value and is a predecessor to this server, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="other">
        /// The other instance.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the provided value represents the same logical server as this value and is a predecessor to this server, otherwise <see langword="false"/>.
        /// </returns>
        public bool IsPredecessorOf(SiloAddress other) => IsSameLogicalSilo(other) && Generation > 0 && Generation < other.Generation;

        /// <inheritdoc/>
        public int CompareTo(SiloAddress? other)
        {
            if (other == null) return 1;
            // Compare Generation first. It gives a cheap and fast way to compare, avoiding allocations 
            // and is also semantically meaningful - older silos (with smaller Generation) will appear first in the comparison order.
            // Only if Generations are the same, go on to compare Ports and IPAddress (which is more expansive to compare).
            // Alternatively, we could compare ConsistentHashCode or UniformHashCode.
            int comp = Generation.CompareTo(other.Generation);
            if (comp != 0) return comp;

            comp = Endpoint.Port.CompareTo(other.Endpoint.Port);
            if (comp != 0) return comp;

            return CompareIpAddresses(Endpoint.Address, other.Endpoint.Address);
        }

        // The comparison code is taken from: http://www.codeproject.com/Articles/26550/Extending-the-IPAddress-object-to-allow-relative-c
        // Also note that this comparison does not handle semantic equivalence  of IPv4 and IPv6 addresses.
        // In particular, 127.0.0.1 and::1 are semantically the same, but not syntactically.
        // For more information refer to: http://stackoverflow.com/questions/16618810/compare-ipv4-addresses-in-ipv6-notation 
        // and http://stackoverflow.com/questions/22187690/ip-address-class-getaddressbytes-method-putting-octets-in-odd-indices-of-the-byt
        // and dual stack sockets, described at https://msdn.microsoft.com/en-us/library/system.net.ipaddress.maptoipv6(v=vs.110).aspx
        private static int CompareIpAddresses(IPAddress one, IPAddress two)
        {
            var f1 = one.AddressFamily;
            var f2 = two.AddressFamily;
            if (f1 != f2)
                return f1 < f2 ? -1 : 1;

            if (f1 == AddressFamily.InterNetwork)
            {
#pragma warning disable CS0618 // Type or member is obsolete
                return one.Address.CompareTo(two.Address);
#pragma warning restore CS0618
            }

            Span<byte> b1 = stackalloc byte[16];
            one.TryWriteBytes(b1, out var len);
            Debug.Assert(len == 16);

            Span<byte> b2 = stackalloc byte[16];
            two.TryWriteBytes(b2, out len);
            Debug.Assert(len == 16);

            return b1.SequenceCompareTo(b2);
        }
    }

    /// <summary>
    /// Functionality for converting <see cref="SiloAddress"/> instances to and from their JSON representation.
    /// </summary>
    public sealed class SiloAddressConverter : JsonConverter<SiloAddress>
    {
        /// <inheritdoc />
        public override SiloAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => SiloAddress.FromParsableString(reader.GetString()!);

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, SiloAddress value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToUtf8String());
    }
}
