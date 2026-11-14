using System.Buffers;
using System.Net;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.GrainReferences;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.General
{
    /// <summary>
    /// Tests for various Orleans identifier types including GrainId, UniqueKey, SiloAddress, and related functionality.
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestArea("Runtime")]
    public class IdentifierTests
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture environment;
        private static Random random => Random.Shared;

        private class A { }

        private class B : A { }

        public IdentifierTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.environment = fixture;
        }

        /// <summary>
        /// Verifies that GrainId.GetUniformHashCode returns a stable value.
        /// </summary>
        [Fact]
        public void GrainIdUniformHashCodeIsStable()
        {
            var id = GrainId.Create("type", "key");
            var hashCode = id.GetUniformHashCode();
            Assert.Equal(831806783u, hashCode);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SiloAddressComparisonDistinguishesIPv6ScopeIds()
        {
            var addressBytes = IPAddress.Parse("fe80::1").GetAddressBytes();
            var lowerScopeAddress = new IPAddress(addressBytes, scopeid: 1);
            var higherScopeAddress = new IPAddress(addressBytes, scopeid: 2);
            var lowerScopeSilo = SiloAddress.New(lowerScopeAddress, 11111, 1);
            var higherScopeSilo = SiloAddress.New(higherScopeAddress, 11111, 1);

            Assert.True(IPAddressComparer.Instance.Compare(lowerScopeAddress, higherScopeAddress) < 0);
            Assert.True(lowerScopeSilo.CompareTo(higherScopeSilo) < 0);
            Assert.NotEqual(lowerScopeSilo, higherScopeSilo);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeyKeyExtGrainCategoryDisallowsNullKeyExtension()
        {
            Assert.Throws<ArgumentNullException>(() =>
            UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: null));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeyKeyExtGrainCategoryDisallowsEmptyKeyExtension()
        {
            Assert.Throws<ArgumentException>(() =>
            UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: ""));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeyKeyExtGrainCategoryDisallowsWhiteSpaceKeyExtension()
        {
            Assert.Throws<ArgumentException>(() =>
            UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: " \t\n\r"));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void ParsingUniqueKeyStringificationShouldReproduceAnIdenticalObject()
        {
            UniqueKey expected1 = UniqueKey.NewKey(Guid.NewGuid());
            string str1 = expected1.ToHexString();
            UniqueKey actual1 = UniqueKey.Parse(str1.AsSpan());
            Assert.Equal(expected1, actual1); // UniqueKey.ToString() and UniqueKey.Parse() failed to reproduce an identical object (case 1).

            string kx3 = "case 3";
            UniqueKey expected3 = UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: kx3);
            string str3 = expected3.ToHexString();
            UniqueKey actual3 = UniqueKey.Parse(str3.AsSpan());
            Assert.Equal(expected3, actual3); // UniqueKey.ToString() and UniqueKey.Parse() failed to reproduce an identical object (case 3).

            long pk = random.Next();
            UniqueKey expected4 = UniqueKey.NewKey(pk);
            string str4 = expected4.ToHexString();
            UniqueKey actual4 = UniqueKey.Parse(str4.AsSpan());
            Assert.Equal(expected4, actual4); // UniqueKey.ToString() and UniqueKey.Parse() failed to reproduce an identical object (case 4).

            pk = random.Next();
            string kx5 = "case 5";
            UniqueKey expected5 = UniqueKey.NewKey(pk, category: UniqueKey.Category.KeyExtGrain, keyExt: kx5);
            string str5 = expected5.ToHexString();
            UniqueKey actual5 = UniqueKey.Parse(str5.AsSpan());
            Assert.Equal(expected5, actual5); // UniqueKey.ToString() and UniqueKey.Parse() failed to reproduce an identical object (case 5).
        }


        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void GrainIdShouldEncodeAndDecodePrimaryKeyGuidCorrectly()
        {
            const int repeat = 100;
            for (int i = 0; i < repeat; ++i)
            {
                Guid expected = Guid.NewGuid();
                GrainId grainId = GrainId.Create(GrainType.Create("foo"), GrainIdKeyExtensions.CreateGuidKey(expected));
                Guid actual = grainId.GetGuidKey();
                Assert.Equal(expected, actual); // Failed to encode and decode grain id
            }
        }

        /// <summary>
        /// Tests GrainId serialization and deserialization through printable string representation.
        /// </summary>
        [TestSuite("SlowBVT")]
        [TestProvider("None")]
        [Theory, TestCategory("SlowBVT"), TestCategory("Identifiers")]
        [InlineData("Guid", "Guid", TestGuid, 0, null, false)]
        [InlineData("Guid with extension", "Guid", TestGuid, 0, "Guid-ExtKey-1", true)]
        [InlineData("Guid with null extension", "Guid", TestGuid, 0, null, true)]
        [InlineData("Integer", "Integer", null, TestInteger, null, false)]
        [InlineData("Integer with extension", "Integer", null, TestInteger, "Long-ExtKey-2", true)]
        [InlineData("Integer with null extension", "Integer", null, TestInteger, null, true)]
        [InlineData("Integer from UniqueKey with extension", "IntegerFromUniqueKey", null, TestInteger, "Long-ExtKey-2", true)]
        public void GrainId_ToFromPrintableString(string testCase, string keyKind, string? guidKey, long integerKey, string? extension, bool useExtensionOverload)
        {
            var grainId = CreateTestGrainId(testCase, keyKind, guidKey, integerKey, extension, useExtensionOverload);
            string str = grainId.ToString();
            var roundTripped = GrainId.Parse(str);

            Assert.Equal(grainId, roundTripped);
        }

        [TestSuite("SlowBVT")]
        [TestProvider("None")]
        [Theory, TestCategory("SlowBVT"), TestCategory("Identifiers")]
        [InlineData("Guid", "Guid", TestGuid, 0, null, false)]
        [InlineData("Guid with extension", "Guid", TestGuid, 0, "Guid-ExtKey-1", true)]
        [InlineData("Guid with null extension", "Guid", TestGuid, 0, null, true)]
        [InlineData("Integer", "Integer", null, TestInteger, null, false)]
        [InlineData("Integer with extension", "Integer", null, TestInteger, "Long-ExtKey-2", true)]
        [InlineData("Integer with null extension", "Integer", null, TestInteger, null, true)]
        [InlineData("Integer from UniqueKey with extension", "IntegerFromUniqueKey", null, TestInteger, "Long-ExtKey-2", true)]
        public void GrainId_TryParseFromPrintableString(string testCase, string keyKind, string? guidKey, long integerKey, string? extension, bool useExtensionOverload)
        {
            var grainId = CreateTestGrainId(testCase, keyKind, guidKey, integerKey, extension, useExtensionOverload);
            string str = grainId.ToString();
            var success = GrainId.TryParse(str, out var roundTripped);

            Assert.True(success);
            Assert.Equal(grainId, roundTripped);
        }

        /// <summary>
        /// Tests GrainId JSON serialization and deserialization round-trip.
        /// </summary>
        [TestSuite("SlowBVT")]
        [TestProvider("None")]
        [Theory, TestCategory("SlowBVT"), TestCategory("Identifiers")]
        [InlineData("Guid", "Guid", TestGuid, 0, null, false)]
        [InlineData("Guid with extension", "Guid", TestGuid, 0, "Guid-ExtKey-1", true)]
        [InlineData("Guid with null extension", "Guid", TestGuid, 0, null, true)]
        [InlineData("Integer", "Integer", null, TestInteger, null, false)]
        [InlineData("Integer with extension", "Integer", null, TestInteger, "Long-ExtKey-2", true)]
        [InlineData("Integer with null extension", "Integer", null, TestInteger, null, true)]
        [InlineData("Integer from UniqueKey with extension", "IntegerFromUniqueKey", null, TestInteger, "Long-ExtKey-2", true)]
        public void GrainId_RoundTripJsonConverter(string testCase, string keyKind, string? guidKey, long integerKey, string? extension, bool useExtensionOverload)
        {
            var grainId = CreateTestGrainId(testCase, keyKind, guidKey, integerKey, extension, useExtensionOverload);
            var serialized = JsonSerializer.Serialize(grainId);
            var deserialized = JsonSerializer.Deserialize<GrainId>(serialized);

            Assert.Equal(grainId, deserialized);
        }

        private const string TestGuid = "83582e92-76a6-4c2d-b0f0-f266875ecf83";
        private const long TestInteger = 0x1234_5678_90AB_CDEF;

        private static GrainId CreateTestGrainId(string testCase, string keyKind, string? guidKey, long integerKey, string? extension, bool useExtensionOverload)
        {
            var grainType = GrainType.Create("test");
            if (keyKind == "Guid")
            {
                var key = Guid.Parse(guidKey ?? throw new ArgumentNullException(nameof(guidKey)));
                return useExtensionOverload
                    ? GrainId.Create(grainType, GrainIdKeyExtensions.CreateGuidKey(key, extension))
                    : GrainId.Create(grainType, GrainIdKeyExtensions.CreateGuidKey(key));
            }

            return keyKind switch
            {
                "Integer" when useExtensionOverload => GrainId.Create(grainType, GrainIdKeyExtensions.CreateIntegerKey(integerKey, extension)),
                "Integer" => GrainId.Create(grainType, GrainIdKeyExtensions.CreateIntegerKey(integerKey)),
                "IntegerFromUniqueKey" => GrainId.Create(grainType, GrainIdKeyExtensions.CreateIntegerKey(UniqueKey.NewKey(integerKey).PrimaryKeyToLong(), extension)),
                _ => throw new ArgumentOutOfRangeException(nameof(keyKind), keyKind, $"Unknown GrainId key kind for test case '{testCase}'"),
            };
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueTypeCodeDataShouldStore32BitsOfInformation()
        {
            const int expected = unchecked((int)0xfabccbaf);
            var uk = UniqueKey.NewKey(0, UniqueKey.Category.None, expected);
            var actual = uk.BaseTypeCode;

            Assert.Equal(expected, actual);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeysShouldPreserveTheirPrimaryKeyValueIfItIsGuid()
        {
            const int all32Bits = unchecked((int)0xffffffff);
            var expectedKey1 = Guid.NewGuid();
            const string expectedKeyExt1 = "1";
            var uk1 = UniqueKey.NewKey(expectedKey1, UniqueKey.Category.KeyExtGrain, all32Bits, expectedKeyExt1);
            string? actualKeyExt1;
            var actualKey1 = uk1.PrimaryKeyToGuid(out actualKeyExt1);
            Assert.Equal(expectedKey1, actualKey1); //"UniqueKey objects should preserve the value of their primary key (Guid case #1).");
            Assert.Equal(expectedKeyExt1, actualKeyExt1); //"UniqueKey objects should preserve the value of their key extension (Guid case #1).");

            var expectedKey2 = Guid.NewGuid();
            const string expectedKeyExt2 = "2";
            var uk2 = UniqueKey.NewKey(expectedKey2, UniqueKey.Category.KeyExtGrain, all32Bits, expectedKeyExt2);
            string? actualKeyExt2;
            var actualKey2 = uk2.PrimaryKeyToGuid(out actualKeyExt2);
            Assert.Equal(expectedKey2, actualKey2); // "UniqueKey objects should preserve the value of their primary key (Guid case #2).");
            Assert.Equal(expectedKeyExt2, actualKeyExt2); // "UniqueKey objects should preserve the value of their key extension (Guid case #2).");
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeysShouldPreserveTheirPrimaryKeyValueIfItIsLong()
        {
            const int all32Bits = unchecked((int)0xffffffff);

            var n1 = random.Next();
            var n2 = random.Next();
            const string expectedKeyExt = "1";
            var expectedKey = unchecked((long)((((ulong)((uint)n1)) << 32) | ((uint)n2)));
            var uk = UniqueKey.NewKey(expectedKey, UniqueKey.Category.KeyExtGrain, all32Bits, expectedKeyExt);

            string? actualKeyExt;
            var actualKey = uk.PrimaryKeyToLong(out actualKeyExt);

            Assert.Equal(expectedKey, actualKey); // "UniqueKey objects should preserve the value of their primary key (long case).");
            Assert.Equal(expectedKeyExt, actualKeyExt); // "UniqueKey objects should preserve the value of their key extension (long case).");
        }

        /// <summary>
        /// Tests GrainId interning functionality to ensure identical IDs reference the same object.
        /// </summary>
        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void ID_Interning_GrainID()
        {
            Guid guid = new Guid();
            GrainId gid1 = LegacyGrainId.FromParsableString(guid.ToString("B"));
            GrainId gid2 = LegacyGrainId.FromParsableString(guid.ToString("N"));
            Assert.Equal(gid1, gid2); // Should be equal GrainId's

            // Round-trip through Serializer
            GrainId gid3 = this.environment.Serializer.Deserialize<GrainId>(environment.Serializer.SerializeToArray(gid1));
            Assert.Equal(gid1, gid3); // Should be equal GrainId's
            Assert.Equal(gid2, gid3); // Should be equal GrainId's
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void ID_Interning_string_equals()
        {
            using var interner = new Interner<string, string>();
            const string str = "1";
            string r1 = interner.FindOrCreate("1", _ => str);
            string r2 = interner.FindOrCreate("1", _ => null!); // Should always be found

            Assert.Equal(r1, r2); // 1: Objects should be equal
            Assert.Same(r1, r2); // 2: Objects should be same / intern'ed

            // Round-trip through Serializer
            string? r3 = this.environment.Serializer.Deserialize<string>(environment.Serializer.SerializeToArray(r1));

            Assert.NotNull(r3);
            Assert.Equal(r1, r3); // 3: Should be equal
            Assert.Equal(r2, r3); // 4: Should be equal
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void ID_Intern_FindOrCreate_derived_class()
        {
            using var interner = new Interner<int, A>();
            var obj1 = new A();
            var obj2 = new B();
            var obj3 = new B();

            var r1 = interner.FindOrCreate(1, _ => obj1);
            Assert.Equal(obj1, r1); // Objects should be equal
            Assert.Same(obj1, r1); // Objects should be same / intern'ed

            var r2 = interner.FindOrCreate(2, _ => obj2);
            Assert.Equal(obj2, r2); // Objects should be equal
            Assert.Same(obj2, r2); // Objects should be same / intern'ed

            // FindOrCreate should not replace instances of same class
            var r3 = interner.FindOrCreate(2, _ => obj3);
            Assert.Same(obj2, r3); // FindOrCreate should return previous object
            Assert.NotSame(obj3, r3); // FindOrCreate should not replace previous object of same class

            // FindOrCreate should not replace cached instances with instances of most derived class
            var r4 = interner.FindOrCreate(1, _ => obj2);
            Assert.Same(obj1, r4); // FindOrCreate return previously cached object
            Assert.NotSame(obj2, r4); // FindOrCreate should not replace previously cached object

            // FindOrCreate should not replace cached instances with instances of less derived class
            var r5 = interner.FindOrCreate(2, _ => obj1);
            Assert.NotSame(obj1, r5); // FindOrCreate should not replace previously cached object
            Assert.Same(obj2, r5); // FindOrCreate return previously cached object
        }

        /// <summary>
        /// Tests SiloAddress interning to ensure identical addresses reference the same object.
        /// </summary>
        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void Interning_SiloAddress()
        {
            //string addrStr1 = "1.2.3.4@11111@1";
            SiloAddress a1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1111), 12345);
            SiloAddress a2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1111), 12345);
            Assert.Equal(a1, a2); // Should be equal SiloAddress's
            Assert.Same(a1, a2); // Should be same / intern'ed SiloAddress object

            // Round-trip through Serializer
            SiloAddress? a3 = this.environment.Serializer.Deserialize<SiloAddress>(environment.Serializer.SerializeToArray(a1));
            Assert.NotNull(a3);
            Assert.Equal(a1, a3); // Should be equal SiloAddress's
            Assert.Equal(a2, a3); // Should be equal SiloAddress's
            Assert.Same(a1, a3); // Should be same / intern'ed SiloAddress object
            Assert.Same(a2, a3); // Should be same / intern'ed SiloAddress object
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void Interning_SiloAddress2()
        {
            SiloAddress a1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1111), 12345);
            SiloAddress a2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 2222), 12345);
            Assert.NotEqual(a1, a2); // Should not be equal SiloAddress's
            Assert.NotSame(a1, a2); // Should not be same / intern'ed SiloAddress object
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void Interning_SiloAddress_Serialization()
        {
            SiloAddress a1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1111), 12345);

            // Round-trip through Serializer
            SiloAddress? a3 = this.environment.Serializer.Deserialize<SiloAddress>(environment.Serializer.SerializeToArray(a1));
            Assert.NotNull(a3);
            Assert.Equal(a1, a3); // Should be equal SiloAddress's
            Assert.Same(a1, a3); // Should be same / intern'ed SiloAddress object
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SiloAddress_ToFrom_ParsableString()
        {
            SiloAddress address1 = SiloAddressUtils.NewLocalSiloAddress(12345);

            string addressStr1 = address1.ToParsableString();
            SiloAddress addressObj1 = SiloAddress.FromParsableString(addressStr1);

            output.WriteLine("Convert -- From: {0} Got result string: '{1}' object: {2}",
                address1, addressStr1, addressObj1);

            Assert.Equal(address1, addressObj1); // SiloAddress equal after To-From-ParsableString

            //const string addressStr2 = "127.0.0.1-11111-144611139";
            const string addressStr2 = "127.0.0.1:11111@144611139";
            SiloAddress addressObj2 = SiloAddress.FromParsableString(addressStr2);
            string addressStr2Out = addressObj2.ToParsableString();

            output.WriteLine("Convert -- From: {0} Got result string: '{1}' object: {2}",
                addressStr2, addressStr2Out, addressObj2);

            Assert.Equal(addressStr2, addressStr2Out); // SiloAddress equal after From-To-ParsableString
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SiloAddress_ParseInterfaces()
        {
            const string addressString = "127.0.0.1:11111@144611139";
            var expected = SiloAddress.FromParsableString(addressString);

            Assert.Same(expected, SiloAddress.Parse(addressString));
            Assert.Same(expected, SiloAddress.Parse(addressString, null));
            Assert.True(SiloAddress.TryParse(addressString, out var parsedWithoutProvider));
            Assert.Same(expected, parsedWithoutProvider);
            Assert.True(SiloAddress.TryParse(addressString, null, out var parsed));
            Assert.Same(expected, parsed);

            var utf8 = Encoding.UTF8.GetBytes(addressString);
            Assert.Same(expected, SiloAddress.Parse(utf8));
            Assert.Same(expected, SiloAddress.Parse(utf8, null));
            Assert.Same(expected, SiloAddress.FromUtf8String(utf8));
            Assert.True(SiloAddress.TryParse(utf8, out var utf8ParsedWithoutProvider));
            Assert.Same(expected, utf8ParsedWithoutProvider);
            Assert.True(SiloAddress.TryParse(utf8, null, out var utf8Parsed));
            Assert.Same(expected, utf8Parsed);

            Assert.False(SiloAddress.TryParse("not-a-silo", out _));
            Assert.False(SiloAddress.TryParse(Encoding.UTF8.GetBytes("not-a-silo"), null, out _));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SiloAddressNewRejectsNullEndpoint()
        {
            Assert.Throws<ArgumentNullException>("ep", () => SiloAddress.New(null!, 1));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SiloAddressNewPreservesNormalizedAndSpecialIdentities()
        {
            foreach (var generation in new[] { 0, -1, int.MinValue, int.MaxValue })
            {
                var address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), generation);
                Assert.Equal(IPAddress.Loopback, address.Endpoint.Address);
                Assert.Equal(11111, address.Endpoint.Port);
                Assert.Equal(generation, address.Generation);
                Assert.Equal(generation < 0, address.IsClient);
                Assert.Equal($"127.0.0.1:11111@{generation}", address.ToParsableString());
                Assert.Same(address, SiloAddress.New(new IPEndPoint(IPAddress.Loopback.MapToIPv6(), 11111), generation));
            }

            var ipv6 = SiloAddress.New(new IPEndPoint(IPAddress.IPv6Loopback, 65535), 1);
            Assert.Equal("::1:65535@1", ipv6.ToParsableString());
            Assert.Same(SiloAddress.Zero, SiloAddress.New(new IPEndPoint(IPAddress.Any, 0), 0));
            Assert.Equal("0.0.0.0:0@0", SiloAddress.Zero.ToParsableString());
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SiloAddressNullComparisonContractsArePreserved()
        {
            var address = SiloAddress.New(IPAddress.Loopback, 11111, 2);
            Assert.False(address.IsSuccessorOf(null!));
            Assert.False(address.IsPredecessorOf(null!));
            Assert.False(address.Equals((SiloAddress?)null));
            Assert.False(address.Equals((object?)null));
            Assert.Equal(1, address.CompareTo(null));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SiloAddressGenerationPredicatesPreserveBoundaries()
        {
            (int Current, int Other, bool Successor, bool Predecessor)[] cases =
            [
                (2, 1, true, false),
                (2, 2, false, false),
                (2, 3, false, true),
                (2, 0, false, false),
                (0, 2, false, false),
                (2, -1, false, false),
                (-1, 2, false, false),
            ];
            foreach (var (current, other, successor, predecessor) in cases)
            {
                var address = SiloAddress.New(IPAddress.Loopback, 11111, current);
                var peer = SiloAddress.New(IPAddress.Loopback, 11111, other);
                Assert.Equal(successor, address.IsSuccessorOf(peer));
                Assert.Equal(predecessor, address.IsPredecessorOf(peer));
                Assert.Equal(current == other, address.Equals(peer));
            }

            var target = SiloAddress.New(IPAddress.Loopback, 11111, 2);
            foreach (var generation in new[] { 1, 3 })
            {
                foreach (var peer in new[]
                {
                    SiloAddress.New(IPAddress.Loopback, 11112, generation),
                    SiloAddress.New(IPAddress.Parse("127.0.0.2"), 11111, generation),
                })
                {
                    Assert.False(target.IsSuccessorOf(peer));
                    Assert.False(target.IsPredecessorOf(peer));
                }
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SiloAddressConverterRejectsNullWriteInputs()
        {
            var converter = new SiloAddressConverter();
            var address = SiloAddress.New(IPAddress.Loopback, 11111, 1);
            using var writer = new Utf8JsonWriter(new ArrayBufferWriter<byte>());
            Assert.Throws<ArgumentNullException>("writer", () => converter.Write(null!, address, null!));
            Assert.Throws<ArgumentNullException>("value", () => converter.Write(writer, null!, null!));
            Assert.Throws<ArgumentNullException>("writer", () => converter.WriteAsPropertyName(null!, address, null!));
            Assert.Throws<ArgumentNullException>("value", () => converter.WriteAsPropertyName(writer, null!, null!));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SiloAddressConverterPreservesJsonTextAndNull()
        {
            var converter = new SiloAddressConverter();
            var address = SiloAddress.New(IPAddress.Loopback, 11111, 12345);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                converter.Write(writer, address, null!);
            }

            const string json = "\"127.0.0.1:11111@12345\"";
            Assert.Equal(json, Encoding.UTF8.GetString(buffer.WrittenSpan));
            Assert.Equal(json, JsonSerializer.Serialize(address));
            Assert.Same(address, JsonSerializer.Deserialize<SiloAddress>(json));

            var propertyBuffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(propertyBuffer))
            {
                writer.WriteStartObject();
                converter.WriteAsPropertyName(writer, address, null!);
                writer.WriteNumberValue(7);
                writer.WriteEndObject();
            }

            const string propertyJson = "{\"127.0.0.1:11111@12345\":7}";
            Assert.Equal(propertyJson, Encoding.UTF8.GetString(propertyBuffer.WrittenSpan));
            var entry = Assert.Single(JsonSerializer.Deserialize<Dictionary<SiloAddress, int>>(propertyJson)!);
            Assert.Same(address, entry.Key);
            Assert.Equal(7, entry.Value);
            Assert.Equal("null", JsonSerializer.Serialize<SiloAddress?>(null));
            Assert.Null(JsonSerializer.Deserialize<SiloAddress>("null"));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SiloAddressParsePreservesNullAndMalformedInputs()
        {
            Assert.Throws<ArgumentNullException>("value", () => SiloAddress.Parse((string)null!));
            Assert.Throws<ArgumentNullException>("value", () => SiloAddress.FromParsableString(null!));
            Assert.False(SiloAddress.TryParse((string?)null, out var nullResult));
            Assert.Null(nullResult);
            foreach (var text in new[] { "", "not-a-silo", "127.0.0.1:65536@1", "127.0.0.1:1@2147483648", "127.0.0.1:1@1 " })
            {
                Assert.Throws<FormatException>(() => SiloAddress.Parse(text));
                Assert.False(SiloAddress.TryParse(text, out var result));
                Assert.Null(result);
                var bytes = Encoding.UTF8.GetBytes(text);
                Assert.Throws<FormatException>(() => SiloAddress.Parse(bytes));
                Assert.False(SiloAddress.TryParse(bytes, out var utf8Result));
                Assert.Null(utf8Result);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void GuidIdGetObjectDataValidatesInfoAndPreservesGuidEntry()
        {
            var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
            var id = GuidId.GetGuidId(guid);
            Assert.Throws<ArgumentNullException>("info", () => id.GetObjectData(null!, default));

#pragma warning disable SYSLIB0050 // Exercise the existing public ISerializable metadata contract, not formatter serialization.
            var info = new SerializationInfo(typeof(GuidId), new FormatterConverter());
#pragma warning restore SYSLIB0050
            id.GetObjectData(info, default);
            Assert.Equal(1, info.MemberCount);
            var entries = info.GetEnumerator();
            Assert.True(entries.MoveNext());
            Assert.Equal("Guid", entries.Name);
            Assert.Equal(typeof(Guid), entries.ObjectType);
            Assert.Equal(guid, Assert.IsType<Guid>(entries.Value));
            Assert.False(entries.MoveNext());
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void GuidIdConverterRejectsNullWriteInputs()
        {
            var converter = new GuidIdConverter();
            var id = GuidId.GetGuidId(Guid.Empty);
            using var writer = new Utf8JsonWriter(new ArrayBufferWriter<byte>());
            Assert.Throws<ArgumentNullException>("writer", () => converter.Write(null!, id, null!));
            Assert.Throws<ArgumentNullException>("value", () => converter.Write(writer, null!, null!));
            Assert.Throws<ArgumentNullException>("writer", () => converter.WriteAsPropertyName(null!, id, null!));
            Assert.Throws<ArgumentNullException>("value", () => converter.WriteAsPropertyName(writer, null!, null!));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void GuidIdConverterPreservesJsonTextAndIdentity()
        {
            var converter = new GuidIdConverter();
            var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
            var id = GuidId.GetGuidId(guid);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                converter.Write(writer, id, null!);
            }

            const string json = "\"00112233445566778899aabbccddeeff\"";
            Assert.Equal(json, Encoding.UTF8.GetString(buffer.WrittenSpan));
            Assert.Equal(json, JsonSerializer.Serialize(id));
            var parsed = Assert.IsType<GuidId>(JsonSerializer.Deserialize<GuidId>(json));
            Assert.Same(id, parsed);
            Assert.Equal(guid, parsed.Guid);
            Assert.Equal("00112233-4455-6677-8899-aabbccddeeff", id.ToString());

            var propertyBuffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(propertyBuffer))
            {
                writer.WriteStartObject();
                converter.WriteAsPropertyName(writer, id, null!);
                writer.WriteNumberValue(7);
                writer.WriteEndObject();
            }

            const string propertyJson = "{\"00112233445566778899aabbccddeeff\":7}";
            Assert.Equal(propertyJson, Encoding.UTF8.GetString(propertyBuffer.WrittenSpan));
            var entry = Assert.Single(JsonSerializer.Deserialize<Dictionary<GuidId, int>>(propertyJson)!);
            Assert.Same(id, entry.Key);
            Assert.Equal(7, entry.Value);
            Assert.Equal("null", JsonSerializer.Serialize<GuidId?>(null));
            Assert.Null(JsonSerializer.Deserialize<GuidId>("null"));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void GuidIdNullAndComparisonContractsArePreserved()
        {
            var id = GuidId.GetGuidId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
            var same = GuidId.GetGuidId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
            var lower = GuidId.GetGuidId(Guid.Empty);
            GuidId? missing = null;
            Assert.False(id.Equals((GuidId?)null));
            Assert.False(id.Equals((object?)null));
            Assert.Equal(1, id.CompareTo(null));
            Assert.True(missing == null);
            Assert.False(missing != null);
            Assert.False(id == missing);
            Assert.False(missing == id);
            Assert.True(id != missing);
            Assert.True(missing != id);
            Assert.True(id.Equals(same));
            Assert.True(id == same);
            Assert.False(id != same);
            Assert.Equal(0, id.CompareTo(same));
            Assert.False(id.Equals(lower));
            Assert.True(id != lower);
            Assert.True(id.CompareTo(lower) > 0);
            Assert.True(lower.CompareTo(id) < 0);
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void GrainIdGetObjectDataValidatesInfoAndPreservesLegacyEntries()
        {
            var id = GrainId.Create("type", "key");
            Assert.Throws<ArgumentNullException>("info", () => id.GetObjectData(null!, default));

            foreach (var value in new[] { id, default })
            {
#pragma warning disable SYSLIB0050 // Exercise the existing ISerializable metadata contract, not formatter serialization.
                var info = new SerializationInfo(typeof(GrainId), new FormatterConverter());
#pragma warning restore SYSLIB0050
                value.GetObjectData(info, default);
                Assert.Equal(4, info.MemberCount);
                var entries = info.GetEnumerator();
                Assert.True(entries.MoveNext());
                Assert.Equal("tv", entries.Name);
                Assert.Equal(value.IsDefault ? typeof(object) : typeof(byte[]), entries.ObjectType);
                Assert.Equal(value.IsDefault ? null : Encoding.UTF8.GetBytes("type"), (byte[]?)entries.Value);
                Assert.True(entries.MoveNext());
                Assert.Equal("th", entries.Name);
                Assert.Equal(typeof(int), entries.ObjectType);
                Assert.Equal(value.Type.GetHashCode(), Assert.IsType<int>(entries.Value));
                Assert.True(entries.MoveNext());
                Assert.Equal("kv", entries.Name);
                Assert.Equal(value.IsDefault ? typeof(object) : typeof(byte[]), entries.ObjectType);
                Assert.Equal(value.IsDefault ? null : Encoding.UTF8.GetBytes("key"), (byte[]?)entries.Value);
                Assert.True(entries.MoveNext());
                Assert.Equal("kh", entries.Name);
                Assert.Equal(typeof(int), entries.ObjectType);
                Assert.Equal(value.Key.GetHashCode(), Assert.IsType<int>(entries.Value));
                Assert.False(entries.MoveNext());

                var constructor = typeof(GrainId).GetConstructor(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    binder: null, [typeof(SerializationInfo), typeof(StreamingContext)], modifiers: null)!;
                var restored = Assert.IsType<GrainId>(constructor.Invoke([info, default(StreamingContext)]));
                Assert.Equal(value.IsDefault ? "/" : "type/key", restored.ToString());
                Assert.Equal(value.IsDefault, restored.IsDefault);
                Assert.Equal(value.Type.GetHashCode(), restored.Type.GetHashCode());
                Assert.Equal(value.Key.GetHashCode(), restored.Key.GetHashCode());
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void GrainIdConverterRejectsNullWriters()
        {
            var converter = new GrainIdJsonConverter();
            Assert.Throws<ArgumentNullException>("writer", () => converter.Write(null!, default, null!));
            Assert.Throws<ArgumentNullException>("writer", () => converter.WriteAsPropertyName(null!, default, null!));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void GrainIdConverterPreservesJsonTextAndPropertyNames()
        {
            var converter = new GrainIdJsonConverter();
            (GrainId Id, string Json)[] cases =
            [
                (GrainId.Create("type", "key"), "\"type/key\""),
                (default, "\"/\""),
                (GrainId.Create("type", "é\"\\尾"), "\"type/\\u00E9\\u0022\\\\\\u5C3E\""),
            ];
            foreach (var (id, json) in cases)
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    converter.Write(writer, id, null!);
                }

                Assert.Equal(json, Encoding.UTF8.GetString(buffer.WrittenSpan));
                Assert.Equal(json, JsonSerializer.Serialize(id));
                var parsed = JsonSerializer.Deserialize<GrainId>(json);
                Assert.Equal(id.Type.AsSpan().ToArray(), parsed.Type.AsSpan().ToArray());
                Assert.Equal(id.Key.AsSpan().ToArray(), parsed.Key.AsSpan().ToArray());

                var propertyBuffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(propertyBuffer))
                {
                    writer.WriteStartObject();
                    converter.WriteAsPropertyName(writer, id, null!);
                    writer.WriteNumberValue(7);
                    writer.WriteEndObject();
                }

                var propertyJson = "{" + json + ":7}";
                Assert.Equal(propertyJson, Encoding.UTF8.GetString(propertyBuffer.WrittenSpan));
                var entry = Assert.Single(JsonSerializer.Deserialize<Dictionary<GrainId, int>>(propertyJson)!);
                Assert.Equal(id.Type.AsSpan().ToArray(), entry.Key.Type.AsSpan().ToArray());
                Assert.Equal(id.Key.AsSpan().ToArray(), entry.Key.Key.AsSpan().ToArray());
                Assert.Equal(7, entry.Value);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeyConverterRejectsNullWriteInputs()
        {
            var converter = new UniqueKeyJsonConverter();
            var key = UniqueKey.NewKey(0);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                Assert.Throws<ArgumentNullException>("writer", () => converter.Write(null!, key, null!));
                Assert.Throws<ArgumentNullException>("value", () => converter.Write(writer, null!, null!));
                Assert.Equal(0, writer.BytesPending);
                converter.Write(writer, key, null!);
            }

            Assert.Equal("\"000000000000000000000000000000000000000000000000\"", Encoding.UTF8.GetString(buffer.WrittenSpan));
            var propertyBuffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(propertyBuffer))
            {
                writer.WriteStartObject();
                var bytesPending = writer.BytesPending;
                Assert.Throws<ArgumentNullException>("writer", () => converter.WriteAsPropertyName(null!, key, null!));
                Assert.Throws<ArgumentNullException>("value", () => converter.WriteAsPropertyName(writer, null!, null!));
                Assert.Equal(bytesPending, writer.BytesPending);
                converter.WriteAsPropertyName(writer, key, null!);
                writer.WriteNumberValue(7);
                writer.WriteEndObject();
            }

            Assert.Equal("{\"000000000000000000000000000000000000000000000000\":7}", Encoding.UTF8.GetString(propertyBuffer.WrittenSpan));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeyConverterPreservesLegacyJsonAndNull()
        {
            var converter = new UniqueKeyJsonConverter();
            (UniqueKey Key, string Json)[] cases =
            [
                (UniqueKey.NewKey(0), "\"000000000000000000000000000000000000000000000000\""),
                (UniqueKey.NewKey(-1, UniqueKey.Category.Grain, 0x12345678), "\"0000000000000000ffffffffffffffff0300000012345678\""),
                (UniqueKey.NewKey(1UL, 2UL, UniqueKey.Category.KeyExtGrain, 0x12345678, "é\"\\尾"),
                    "\"000000000000000100000000000000020600000012345678\\u002B\\u00E9\\u0022\\\\\\u5C3E\""),
            ];
            foreach (var (key, json) in cases)
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    converter.Write(writer, key, null!);
                }

                Assert.Equal(json, Encoding.UTF8.GetString(buffer.WrittenSpan));
                Assert.Equal(json, JsonSerializer.Serialize(key));
                var parsed = Assert.IsType<UniqueKey>(JsonSerializer.Deserialize<UniqueKey>(json));
                Assert.Equal(key.N0, parsed.N0);
                Assert.Equal(key.N1, parsed.N1);
                Assert.Equal(key.TypeCodeData, parsed.TypeCodeData);
                Assert.Equal(key.KeyExt, parsed.KeyExt);

                var propertyBuffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(propertyBuffer))
                {
                    writer.WriteStartObject();
                    converter.WriteAsPropertyName(writer, key, null!);
                    writer.WriteNumberValue(7);
                    writer.WriteEndObject();
                }

                var propertyJson = "{" + json + ":7}";
                Assert.Equal(propertyJson, Encoding.UTF8.GetString(propertyBuffer.WrittenSpan));
                var entry = Assert.Single(JsonSerializer.Deserialize<Dictionary<UniqueKey, int>>(propertyJson)!);
                Assert.Equal(key.N0, entry.Key.N0);
                Assert.Equal(key.N1, entry.Key.N1);
                Assert.Equal(key.TypeCodeData, entry.Key.TypeCodeData);
                Assert.Equal(key.KeyExt, entry.Key.KeyExt);
                Assert.Equal(7, entry.Value);
                Assert.False(key.Equals((UniqueKey?)null));
                Assert.False(key.Equals((object?)null));
                Assert.Equal(1, key.CompareTo(null));
            }

            Assert.Equal("null", JsonSerializer.Serialize<UniqueKey?>(null));
            Assert.Null(JsonSerializer.Deserialize<UniqueKey>("null"));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SystemTargetGrainIdRejectsNullAddresses()
        {
            var kind = SystemTargetGrainId.CreateGrainType("boundary");
            var target = SystemTargetGrainId.Create(kind, SiloAddress.Zero);
            Assert.Throws<ArgumentNullException>("address", () => SystemTargetGrainId.Create(kind, null!));
            Assert.Throws<ArgumentNullException>("address", () => SystemTargetGrainId.Create(kind, null!, "extension"));
            Assert.Throws<ArgumentNullException>("siloAddress", () => target.WithSiloAddress(null!));
            Assert.Throws<ArgumentNullException>("address", () => SystemTargetGrainId.CreateGrainServiceGrainId(1, "service", null!));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SystemTargetGrainIdPreservesKeyExtensionsWhenRetargeting()
        {
            var kind = SystemTargetGrainId.CreateGrainType("boundary");
            var originalAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1);
            var replacement = SiloAddress.New(IPAddress.IPv6Loopback, 22222, int.MaxValue);
            var plain = SystemTargetGrainId.Create(kind, originalAddress);
            Assert.Equal("sys.svc.boundary", plain.GrainId.Type.ToString());
            Assert.Equal("127.0.0.1:11111@1", plain.GrainId.Key.ToString());
            Assert.False(plain.Equals((object?)null));

            foreach (var extension in new string?[] { null, "", "é+part++尾" })
            {
                var original = SystemTargetGrainId.Create(kind, originalAddress, extension);
                var suffix = extension is null ? "" : "+" + extension;
                Assert.Equal(Encoding.UTF8.GetBytes("127.0.0.1:11111@1" + suffix), original.GrainId.Key.AsSpan().ToArray());
                Assert.Equal(extension is null, plain == original);
                var updated = original.WithSiloAddress(replacement);
                Assert.Equal(kind, updated.GrainId.Type);
                Assert.Equal(Encoding.UTF8.GetBytes("::1:22222@2147483647" + suffix), updated.GrainId.Key.AsSpan().ToArray());
                Assert.Same(replacement, updated.GetSiloAddress());
                Assert.Same(originalAddress, original.GetSiloAddress());
                Assert.Equal(Encoding.UTF8.GetBytes("127.0.0.1:11111@1" + suffix), original.GrainId.Key.AsSpan().ToArray());
                Assert.True(SystemTargetGrainId.TryParse(updated.GrainId, out var parsed));
                Assert.Equal(updated, parsed);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SystemTargetGrainIdPreservesGrainServiceIdentity()
        {
            var address = SiloAddress.New(IPAddress.Loopback, 11111, 12345);
            (int Code, string Text)[] codes =
            [
                (0, "00000000"),
                (int.MaxValue, "7FFFFFFF"),
                (int.MinValue, "80000000"),
                (-1, "FFFFFFFF"),
            ];
            foreach (var (code, text) in codes)
            {
                foreach (var systemId in new string?[] { null, "", "é+service" })
                {
                    var id = SystemTargetGrainId.CreateGrainServiceGrainId(code, systemId!, address);
                    Assert.Equal(Encoding.UTF8.GetBytes("sys.svc.user." + text + systemId), id.Type.AsSpan().ToArray());
                    Assert.Equal(Encoding.UTF8.GetBytes("127.0.0.1:11111@12345"), id.Key.AsSpan().ToArray());
                    Assert.True(SystemTargetGrainId.TryParse(id, out var target));
                    Assert.Same(address, target.GetSiloAddress());
                }
            }
        }

        /// <summary>
        /// Tests GrainReference creation, serialization, and round-trip operations.
        /// </summary>
        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT"), TestCategory("Identifiers"), TestCategory("GrainReference")]
        public void GrainReference_Test1()
        {
            Guid guid = Guid.NewGuid();
            GrainId regularGrainId = LegacyGrainId.GetGrainIdForTesting(guid);
            GrainReference grainRef = (GrainReference)this.environment.InternalGrainFactory.GetGrain(regularGrainId);
            TestGrainReference(grainRef);

            grainRef = (GrainReference)this.environment.InternalGrainFactory.GetGrain(regularGrainId);
            TestGrainReference(grainRef);
        }

        private void TestGrainReference(GrainReference grainRef)
        {
            GrainReference? roundTripped = RoundTripGrainReferenceToKey(grainRef);
            Assert.Equal(grainRef, roundTripped); // GrainReference.ToKeyString

            roundTripped = this.environment.Serializer.Deserialize<GrainReference>(environment.Serializer.SerializeToArray(grainRef));
            Assert.NotNull(roundTripped);
            Assert.Equal(grainRef, roundTripped); // GrainReference.OrleansSerializer
        }

        private GrainReference RoundTripGrainReferenceToKey(GrainReference input)
        {
            string str = input.GrainId.ToString();
            GrainReference output = this.environment.Services.GetRequiredService<GrainReferenceActivator>().CreateReference(GrainId.Parse(str), default);
            return output;
        }
    }
}
