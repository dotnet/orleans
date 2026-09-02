using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Orleans.Runtime;
using Orleans.Runtime.ClusterServices;
using TestExtensions;
using Xunit;

namespace UnitTests.ClusterServices;

[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class ClusterServiceConfigurationTests
{
    private const string AssignmentStrategy = "uniform-hash-ring/v1";
    private const string KnownFingerprint = "F8C6F40A085E887070C5451171EFBD84FD9DA8C3E75585F3CF027E791CA1904E";

    [Fact]
    public void Constructor_PreservesDescriptorFields()
    {
        var configuration = new ClusterServiceConfiguration(
            "orders",
            protocolVersion: 17,
            partitionsPerSilo: 31,
            "rendezvous/v2");

        Assert.Equal("orders", configuration.ServiceId);
        Assert.Equal(17, configuration.ProtocolVersion);
        Assert.Equal(31, configuration.PartitionsPerSilo);
        Assert.Equal("rendezvous/v2", configuration.AssignmentStrategy);
    }

    [Fact]
    public void Fingerprint_MatchesIndependentKnownDigest()
    {
        var configuration = CreateConfiguration();
        var oracle = ConfigurationFingerprintOracle.Compute(
            "test-service",
            protocolVersion: 1,
            partitionsPerSilo: 1,
            AssignmentStrategy);

        Assert.Equal(KnownFingerprint, oracle);
        Assert.Equal(KnownFingerprint, configuration.Fingerprint);
    }

    [Fact]
    public void Fingerprint_IsUppercaseSixtyFourCharacterHex()
    {
        var fingerprint = CreateConfiguration().Fingerprint;

        Assert.Equal(64, fingerprint.Length);
        Assert.Matches("^[0-9A-F]{64}$", fingerprint);
    }

    [Fact]
    public void Fingerprint_IsCultureInvariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            foreach (var cultureName in new[] { "", "tr-TR", "fr-FR", "ar-SA" })
            {
                var culture = cultureName.Length == 0
                    ? CultureInfo.InvariantCulture
                    : CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;

                var configuration = CreateConfiguration(protocolVersion: 1234, partitionsPerSilo: 5678);
                var oracle = ConfigurationFingerprintOracle.Compute(
                    "test-service",
                    protocolVersion: 1234,
                    partitionsPerSilo: 5678,
                    AssignmentStrategy);

                Assert.Equal(oracle, configuration.Fingerprint);
                Assert.Equal("0AA8BC0B0F07B83BDDEA89435CD2C0A600CE605D3D9D76A62A4F09E60CF317EA", configuration.Fingerprint);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        Assert.Same(originalCulture, CultureInfo.CurrentCulture);
        Assert.Same(originalUiCulture, CultureInfo.CurrentUICulture);
    }

    [Fact]
    public void Fingerprint_ChangesForEachCompatibilityInput()
    {
        var baseline = CreateConfiguration();

        Assert.NotEqual(baseline.Fingerprint, CreateConfiguration(serviceId: "other-service").Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, CreateConfiguration(protocolVersion: 2).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, CreateConfiguration(partitionsPerSilo: 2).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, CreateConfiguration(assignmentStrategy: "uniform-hash-ring/v2").Fingerprint);
        Assert.Equal(baseline.Fingerprint, CreateConfiguration().Fingerprint);
    }

    [Fact]
    public void Fingerprint_LengthPrefixesPreventNewlineBoundaryCollisions()
    {
        var newlineInServiceId = CreateConfiguration(
            serviceId: "a\n1",
            protocolVersion: 2,
            partitionsPerSilo: 3,
            assignmentStrategy: "b");
        var newlineInAssignmentStrategy = CreateConfiguration(
            serviceId: "a",
            protocolVersion: 1,
            partitionsPerSilo: 2,
            assignmentStrategy: "3\nb");

        Assert.Equal(
            "6AA88D498B9AB2928A2414222ED2D15FC102499BB1D96AAF6A65257807B779F6",
            newlineInServiceId.Fingerprint);
        Assert.Equal(
            "0888E890389AB508BE73218B38933C0928D77C29E6DC465A6BECBEFE5A2CDC3F",
            newlineInAssignmentStrategy.Fingerprint);
        Assert.NotEqual(newlineInServiceId.Fingerprint, newlineInAssignmentStrategy.Fingerprint);
    }

    [Fact]
    public void Constructor_RejectsNullServiceId()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new ClusterServiceConfiguration(null!, 1, 1, AssignmentStrategy));

        Assert.Equal("serviceId", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Constructor_RejectsEmptyOrWhitespaceServiceId(string serviceId)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ClusterServiceConfiguration(serviceId, 1, 1, AssignmentStrategy));

        Assert.Equal("serviceId", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_RejectsNonPositiveProtocolVersion(int protocolVersion)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClusterServiceConfiguration("test-service", protocolVersion, 1, AssignmentStrategy));

        Assert.Equal("protocolVersion", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_RejectsNonPositivePartitionsPerSilo(int partitionsPerSilo)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClusterServiceConfiguration("test-service", 1, partitionsPerSilo, AssignmentStrategy));

        Assert.Equal("partitionsPerSilo", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsNullAssignmentStrategy()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new ClusterServiceConfiguration("test-service", 1, 1, null!));

        Assert.Equal("assignmentStrategy", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Constructor_RejectsEmptyOrWhitespaceAssignmentStrategy(string assignmentStrategy)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ClusterServiceConfiguration("test-service", 1, 1, assignmentStrategy));

        Assert.Equal("assignmentStrategy", exception.ParamName);
    }

    [Fact]
    public void ViewId_IsDirectSuccessorForExactlyNextMembershipVersion()
    {
        var previous = CreateViewId(membershipVersion: 10);
        var successor = CreateViewId(membershipVersion: 11);

        Assert.True(successor.IsDirectSuccessorOf(previous));
        Assert.False(previous.IsDirectSuccessorOf(successor));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(9)]
    public void ViewId_IsNotDirectSuccessorForSameSkippedOrOlderMembershipVersion(long membershipVersion)
    {
        var previous = CreateViewId(membershipVersion: 10);
        var candidate = CreateViewId(membershipVersion);

        Assert.False(candidate.IsDirectSuccessorOf(previous));
    }

    [Fact]
    public void ViewId_IsNotDirectSuccessorWhenProtocolDiffers()
    {
        var previous = CreateViewId(membershipVersion: 10, protocolVersion: 7);
        var candidate = CreateViewId(membershipVersion: 11, protocolVersion: 8);

        Assert.False(candidate.IsDirectSuccessorOf(previous));
    }

    [Fact]
    public void ViewId_IsNotDirectSuccessorWhenFingerprintDiffers()
    {
        var previous = CreateViewId(membershipVersion: 10, configurationFingerprint: KnownFingerprint);
        var candidate = CreateViewId(membershipVersion: 11, configurationFingerprint: new string('A', 64));

        Assert.False(candidate.IsDirectSuccessorOf(previous));
    }

    private static ClusterServiceConfiguration CreateConfiguration(
        string serviceId = "test-service",
        int protocolVersion = 1,
        int partitionsPerSilo = 1,
        string assignmentStrategy = AssignmentStrategy) =>
        new(serviceId, protocolVersion, partitionsPerSilo, assignmentStrategy);

    private static ClusterServiceViewId CreateViewId(
        long membershipVersion,
        int protocolVersion = 7,
        string configurationFingerprint = KnownFingerprint) =>
        new(new MembershipVersion(membershipVersion), protocolVersion, configurationFingerprint);

    private static class ConfigurationFingerprintOracle
    {
        private const string HexDigits = "0123456789ABCDEF";

        public static string Compute(
            string serviceId,
            int protocolVersion,
            int partitionsPerSilo,
            string assignmentStrategy)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendString(hash, serviceId);
            AppendInt32(hash, protocolVersion);
            AppendInt32(hash, partitionsPerSilo);
            AppendString(hash, assignmentStrategy);
            return ToUpperHex(hash.GetHashAndReset());
        }

        private static void AppendString(IncrementalHash hash, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> lengthPrefix = stackalloc byte[5];
            var prefixLength = Write7BitEncodedInt(lengthPrefix, bytes.Length);
            hash.AppendData(lengthPrefix[..prefixLength]);
            hash.AppendData(bytes);
        }

        private static void AppendInt32(IncrementalHash hash, int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            hash.AppendData(bytes);
        }

        private static int Write7BitEncodedInt(Span<byte> destination, int value)
        {
            var remaining = (uint)value;
            var index = 0;
            while (remaining >= 0x80)
            {
                destination[index++] = (byte)((remaining & 0x7F) | 0x80);
                remaining >>= 7;
            }

            destination[index++] = (byte)remaining;
            return index;
        }

        private static string ToUpperHex(byte[] bytes) =>
            string.Create(bytes.Length * 2, bytes, static (destination, source) =>
            {
                for (var index = 0; index < source.Length; index++)
                {
                    destination[index * 2] = HexDigits[source[index] >> 4];
                    destination[(index * 2) + 1] = HexDigits[source[index] & 0x0F];
                }
            });
    }
}
