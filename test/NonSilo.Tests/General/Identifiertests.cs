using System;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class IdentifierTests
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture environment;
        private static readonly Random random = new Random();

        class A { }
        class B : A { }
        
        public IdentifierTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.environment = fixture;
        }

        [Fact]
        public void GrainIdUniformHashCodeIsStable()
        {
            var id = GrainId.Create("type", "key");
            var hashCode = id.GetUniformHashCode();
            Assert.Equal((uint)2618661990, hashCode);
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeyToByteArrayWithKeyExt()
        {
            var key = UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: "hello world");

            var result = key.ToByteArray();

            var sw = new BinaryTokenStreamWriter();
            sw.Write(key);
            var expected = sw.ToByteArray();

            Assert.Equal(expected.Length, result.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], result[i]);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeyToByteArrayWithoutKeyExt()
        {
            var key = UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.Client);

            var result = key.ToByteArray();

            var sw = new BinaryTokenStreamWriter();
            sw.Write(key);
            var expected = sw.ToByteArray();

            Assert.Equal(expected.Length, result.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], result[i]);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void SiloAddressGetUniformHashCodes()
        {
            int numberofHash = 3;
            var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 8080), 26);

            var result = siloAddress.GetUniformHashCodes(numberofHash);

            for (int i = 0; i < numberofHash; i++)
            {
                var sw = new BinaryTokenStreamWriter();
                sw.Write(siloAddress);
                sw.Write(i);
                var expected = JenkinsHash.ComputeHash(sw.ToByteArray());

                Assert.Equal(expected, result[i]);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeyKeyExtGrainCategoryDisallowsNullKeyExtension()
        {
            Assert.Throws<ArgumentNullException>(() =>
            UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: null));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeyKeyExtGrainCategoryDisallowsEmptyKeyExtension()
        {
            Assert.Throws<ArgumentException>(() =>
            UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: ""));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeyKeyExtGrainCategoryDisallowsWhiteSpaceKeyExtension()
        {
            Assert.Throws<ArgumentException>(() =>
            UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: " \t\n\r"));
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeySerializationShouldReproduceAnIdenticalObject()
        {
            {
                var expected = UniqueKey.NewKey(Guid.NewGuid());
                BinaryTokenStreamWriter writer = new BinaryTokenStreamWriter();
                writer.Write(expected);
                BinaryTokenStreamReader reader = new BinaryTokenStreamReader(writer.ToBytes());
                var actual = reader.ReadUniqueKey();
                Assert.Equal(expected, actual); // UniqueKey.Serialize() and UniqueKey.Deserialize() failed to reproduce an identical object (case #1).
            }

            {
                var kx = random.Next().ToString(CultureInfo.InvariantCulture);
                var expected = UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: kx);
                BinaryTokenStreamWriter writer = new BinaryTokenStreamWriter();
                writer.Write(expected);
                BinaryTokenStreamReader reader = new BinaryTokenStreamReader(writer.ToBytes());
                var actual = reader.ReadUniqueKey();
                Assert.Equal(expected, actual); // UniqueKey.Serialize() and UniqueKey.Deserialize() failed to reproduce an identical object (case #2).
            }

            {
                var kx = random.Next().ToString(CultureInfo.InvariantCulture) + new String('*', 400);
                var expected = UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: kx);
                BinaryTokenStreamWriter writer = new BinaryTokenStreamWriter();
                writer.Write(expected);
                BinaryTokenStreamReader reader = new BinaryTokenStreamReader(writer.ToBytes());
                var actual = reader.ReadUniqueKey();
                Assert.Equal(expected, actual); // UniqueKey.Serialize() and UniqueKey.Deserialize() failed to reproduce an identical object (case #3).
            }
        }

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

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void GrainId_ToFromPrintableString()
        {
            Guid guid = Guid.NewGuid();
            GrainId grainId = GrainId.Create(GrainType.Create("test"), GrainIdKeyExtensions.CreateGuidKey(guid));
            GrainId roundTripped = RoundTripGrainIdToParsable(grainId);
            Assert.Equal(grainId, roundTripped); // GrainId.ToPrintableString -- Guid key

            string extKey = "Guid-ExtKey-1";
            guid = Guid.NewGuid();
            grainId = GrainId.Create(GrainType.Create("test"), GrainIdKeyExtensions.CreateGuidKey(guid, extKey));
            roundTripped = RoundTripGrainIdToParsable(grainId);
            Assert.Equal(grainId, roundTripped); // GrainId.ToPrintableString -- Guid key + Extended Key

            grainId = GrainId.Create(GrainType.Create("test"), GrainIdKeyExtensions.CreateGuidKey(guid, null));
            roundTripped = RoundTripGrainIdToParsable(grainId);
            Assert.Equal(grainId, roundTripped); // GrainId.ToPrintableString -- Guid key + null Extended Key

            long key = random.Next();
            grainId = GrainId.Create(GrainType.Create("test"), GrainIdKeyExtensions.CreateIntegerKey(key));
            roundTripped = RoundTripGrainIdToParsable(grainId);
            Assert.Equal(grainId, roundTripped); // GrainId.ToPrintableString -- Int64 key

            extKey = "Long-ExtKey-2";
            key = random.Next();
            grainId = GrainId.Create(GrainType.Create("test"), GrainIdKeyExtensions.CreateIntegerKey(key, extKey));
            roundTripped = RoundTripGrainIdToParsable(grainId);
            Assert.Equal(grainId, roundTripped); // GrainId.ToPrintableString -- Int64 key + Extended Key

            key = UniqueKey.NewKey(key).PrimaryKeyToLong();
            grainId = GrainId.Create(GrainType.Create("test"), GrainIdKeyExtensions.CreateIntegerKey(key, extKey));
            roundTripped = RoundTripGrainIdToParsable(grainId);
            Assert.Equal(grainId, roundTripped); // GrainId.ToPrintableString -- Int64 key + null Extended Key
        }

        private GrainId RoundTripGrainIdToParsable(GrainId input)
        {
            string str = input.ToString();
            return GrainId.Parse(str);
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueTypeCodeDataShouldStore32BitsOfInformation()
        {
            const int expected = unchecked((int)0xfabccbaf);
            var uk = UniqueKey.NewKey(0, UniqueKey.Category.None, expected);
            var actual = uk.BaseTypeCode;

            Assert.Equal(expected, actual);
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeysShouldPreserveTheirPrimaryKeyValueIfItIsGuid()
        {
            const int all32Bits = unchecked((int)0xffffffff);
            var expectedKey1 = Guid.NewGuid();
            const string expectedKeyExt1 = "1";
            var uk1 = UniqueKey.NewKey(expectedKey1, UniqueKey.Category.KeyExtGrain, all32Bits, expectedKeyExt1);
            string actualKeyExt1;
            var actualKey1 = uk1.PrimaryKeyToGuid(out actualKeyExt1);
            Assert.Equal(expectedKey1, actualKey1); //"UniqueKey objects should preserve the value of their primary key (Guid case #1).");
            Assert.Equal(expectedKeyExt1, actualKeyExt1); //"UniqueKey objects should preserve the value of their key extension (Guid case #1).");

            var expectedKey2 = Guid.NewGuid();
            const string expectedKeyExt2 = "2";
            var uk2 = UniqueKey.NewKey(expectedKey2, UniqueKey.Category.KeyExtGrain, all32Bits, expectedKeyExt2);
            string actualKeyExt2;
            var actualKey2 = uk2.PrimaryKeyToGuid(out actualKeyExt2);
            Assert.Equal(expectedKey2, actualKey2); // "UniqueKey objects should preserve the value of their primary key (Guid case #2).");
            Assert.Equal(expectedKeyExt2, actualKeyExt2); // "UniqueKey objects should preserve the value of their key extension (Guid case #2).");
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void UniqueKeysShouldPreserveTheirPrimaryKeyValueIfItIsLong()
        {
            const int all32Bits = unchecked((int)0xffffffff);

            var n1 = random.Next();
            var n2 = random.Next();
            const string expectedKeyExt = "1";
            var expectedKey = unchecked((long)((((ulong)((uint)n1)) << 32) | ((uint)n2)));
            var uk = UniqueKey.NewKey(expectedKey, UniqueKey.Category.KeyExtGrain, all32Bits, expectedKeyExt);

            string actualKeyExt;
            var actualKey = uk.PrimaryKeyToLong(out actualKeyExt);

            Assert.Equal(expectedKey, actualKey); // "UniqueKey objects should preserve the value of their primary key (long case).");
            Assert.Equal(expectedKeyExt, actualKeyExt); // "UniqueKey objects should preserve the value of their key extension (long case).");
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void ID_HashCorrectness()
        {
            // This tests that our optimized Jenkins hash computes the same value as the reference implementation
            int testCount = 1000;
            for (int i = 0; i < testCount; i++)
            {
                byte[] byteData = new byte[24];
                random.NextBytes(byteData);
                ulong u1 = BitConverter.ToUInt64(byteData, 0);
                ulong u2 = BitConverter.ToUInt64(byteData, 8);
                ulong u3 = BitConverter.ToUInt64(byteData, 16);
                var referenceHash = JenkinsHash.ComputeHash(byteData);
                var optimizedHash = JenkinsHash.ComputeHash(u1, u2, u3);
                Assert.Equal(referenceHash,  optimizedHash);  //  "Optimized hash value doesn't match the reference value for inputs {0}, {1}, {2}", u1, u2, u3
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void ID_Interning_GrainID()
        {
            Guid guid = new Guid();
            GrainId gid1 = LegacyGrainId.FromParsableString(guid.ToString("B"));
            GrainId gid2 = LegacyGrainId.FromParsableString(guid.ToString("N"));
            Assert.Equal(gid1, gid2); // Should be equal GrainId's

            // Round-trip through Serializer
            GrainId gid3 = (GrainId)this.environment.SerializationManager.RoundTripSerializationForTesting(gid1);
            Assert.Equal(gid1, gid3); // Should be equal GrainId's
            Assert.Equal(gid2, gid3); // Should be equal GrainId's
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void ID_Interning_string_equals()
        {
            using var interner = new Interner<string, string>();
            const string str = "1";
            string r1 = interner.FindOrCreate("1", _ => str);
            string r2 = interner.FindOrCreate("1", _ => null); // Should always be found

            Assert.Equal(r1, r2); // 1: Objects should be equal
            Assert.Same(r1, r2); // 2: Objects should be same / intern'ed

            // Round-trip through Serializer
            string r3 = (string)this.environment.SerializationManager.RoundTripSerializationForTesting(r1);

            Assert.Equal(r1, r3); // 3: Should be equal
            Assert.Equal(r2, r3); // 4: Should be equal
        }

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

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void Interning_SiloAddress()
        {
            //string addrStr1 = "1.2.3.4@11111@1";
            SiloAddress a1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1111), 12345);
            SiloAddress a2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1111), 12345);
            Assert.Equal(a1, a2); // Should be equal SiloAddress's
            Assert.Same(a1, a2); // Should be same / intern'ed SiloAddress object

            // Round-trip through Serializer
            SiloAddress a3 = (SiloAddress)this.environment.SerializationManager.RoundTripSerializationForTesting(a1);
            Assert.Equal(a1, a3); // Should be equal SiloAddress's
            Assert.Equal(a2, a3); // Should be equal SiloAddress's
            Assert.Same(a1, a3); // Should be same / intern'ed SiloAddress object
            Assert.Same(a2, a3); // Should be same / intern'ed SiloAddress object
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void Interning_SiloAddress2()
        {
            SiloAddress a1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1111), 12345);
            SiloAddress a2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 2222), 12345);
            Assert.NotEqual(a1, a2); // Should not be equal SiloAddress's
            Assert.NotSame(a1, a2); // Should not be same / intern'ed SiloAddress object
        }

        [Fact, TestCategory("BVT"), TestCategory("Identifiers")]
        public void Interning_SiloAddress_Serialization()
        {
            SiloAddress a1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1111), 12345);

            // Round-trip through Serializer
            SiloAddress a3 = (SiloAddress)this.environment.SerializationManager.RoundTripSerializationForTesting(a1);
            Assert.Equal(a1, a3); // Should be equal SiloAddress's
            Assert.Same(a1, a3); // Should be same / intern'ed SiloAddress object
        }

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
            GrainReference roundTripped = RoundTripGrainReferenceToKey(grainRef);
            Assert.Equal(grainRef, roundTripped); // GrainReference.ToKeyString

            roundTripped = this.environment.SerializationManager.RoundTripSerializationForTesting(grainRef);
            Assert.Equal(grainRef, roundTripped); // GrainReference.OrleansSerializer
        }

        private GrainReference RoundTripGrainReferenceToKey(GrainReference input)
        {
            string str = input.ToKeyString();
            GrainReference output = this.environment.Services.GetRequiredService<GrainReferenceKeyStringConverter>().FromKeyString(str);
            return output;
        }
    }
}
