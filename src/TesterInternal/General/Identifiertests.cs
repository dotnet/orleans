using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.TestingHost;

namespace UnitTests.General
{
    [TestClass]
    public class Identifiertests
    {
        public TestContext TestContext { get; set; }

        private static readonly Random random = new Random();

        class A { }
        class B : A { }

        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void ID_IsSystem()
        {
            GrainId testGrain = Constants.DirectoryServiceId;
            Console.WriteLine("Testing GrainID " + testGrain);
            Assert.IsTrue(testGrain.IsSystemTarget, "System grain ID is not flagged as a system ID");

            GrainId sGrain = (GrainId) SerializationManager.DeepCopy(testGrain);
            Console.WriteLine("Testing GrainID " + sGrain);
            Assert.IsTrue(sGrain.IsSystemTarget, "String round-trip grain ID is not flagged as a system ID");
            Assert.AreEqual(testGrain, sGrain, "Should be equivalent GrainId object");
            Assert.AreSame(testGrain, sGrain, "Should be same / intern'ed GrainId object");

            ActivationId testActivation = ActivationId.GetSystemActivation(testGrain, SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 2456), 0));
            Console.WriteLine("Testing ActivationID " + testActivation);
            Assert.IsTrue(testActivation.IsSystem, "System activation ID is not flagged as a system ID");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        [ExpectedException(typeof(ArgumentNullException))]
        public void UniqueKeyKeyExtGrainCategoryDisallowsNullKeyExtension()
        {
            UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: null);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        [ExpectedException(typeof(ArgumentException))]
        public void UniqueKeyKeyExtGrainCategoryDisallowsEmptyKeyExtension()
        {
            UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: "");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        [ExpectedException(typeof(ArgumentException))]
        public void UniqueKeyKeyExtGrainCategoryDisallowsWhiteSpaceKeyExtension()
        {
            UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: " \t\n\r");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void UniqueKeySerializationShouldReproduceAnIdenticalObject()
        {
            {
                var expected = UniqueKey.NewKey(Guid.NewGuid());
                BinaryTokenStreamWriter writer = new BinaryTokenStreamWriter();
                writer.Write(expected);
                BinaryTokenStreamReader reader = new BinaryTokenStreamReader(writer.ToBytes());
                var actual = reader.ReadUniqueKey();
                Assert.AreEqual(expected, actual, "UniqueKey.Serialize() and UniqueKey.Deserialize() failed to reproduce an identical object (case #1).");
            }

            {
                var kx = random.Next().ToString(CultureInfo.InvariantCulture);
                var expected = UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: kx);
                BinaryTokenStreamWriter writer = new BinaryTokenStreamWriter();
                writer.Write(expected);
                BinaryTokenStreamReader reader = new BinaryTokenStreamReader(writer.ToBytes());
                var actual = reader.ReadUniqueKey();
                Assert.AreEqual(expected, actual, "UniqueKey.Serialize() and UniqueKey.Deserialize() failed to reproduce an identical object (case #2).");
            }

            {
                var kx = random.Next().ToString(CultureInfo.InvariantCulture) + new String('*', 400);
                var expected = UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: kx);
                BinaryTokenStreamWriter writer = new BinaryTokenStreamWriter();
                writer.Write(expected);
                BinaryTokenStreamReader reader = new BinaryTokenStreamReader(writer.ToBytes());
                var actual = reader.ReadUniqueKey();
                Assert.AreEqual(expected, actual, "UniqueKey.Serialize() and UniqueKey.Deserialize() failed to reproduce an identical object (case #3).");
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void ParsingUniqueKeyStringificationShouldReproduceAnIdenticalObject()
        {
            UniqueKey expected1 = UniqueKey.NewKey(Guid.NewGuid());
            string str1 = expected1.ToHexString();
            UniqueKey actual1 = UniqueKey.Parse(str1);
            Assert.AreEqual(expected1, actual1, "UniqueKey.ToString() and UniqueKey.Parse() failed to reproduce an identical object (case 1).");

            string kx3 = "case 3";
            UniqueKey expected3 = UniqueKey.NewKey(Guid.NewGuid(), category: UniqueKey.Category.KeyExtGrain, keyExt: kx3);
            string str3 = expected3.ToHexString();
            UniqueKey actual3 = UniqueKey.Parse(str3);
            Assert.AreEqual(expected3, actual3, "UniqueKey.ToString() and UniqueKey.Parse() failed to reproduce an identical object (case 3).");

            long pk = random.Next();
            UniqueKey expected4 = UniqueKey.NewKey(pk);
            string str4 = expected4.ToHexString();
            UniqueKey actual4 = UniqueKey.Parse(str4);
            Assert.AreEqual(expected4, actual4, "UniqueKey.ToString() and UniqueKey.Parse() failed to reproduce an identical object (case 4).");

            pk = random.Next();
            string kx5 = "case 5";
            UniqueKey expected5 = UniqueKey.NewKey(pk, category: UniqueKey.Category.KeyExtGrain, keyExt: kx5);
            string str5 = expected5.ToHexString();
            UniqueKey actual5 = UniqueKey.Parse(str5);
            Assert.AreEqual(expected5, actual5, "UniqueKey.ToString() and UniqueKey.Parse() failed to reproduce an identical object (case 5).");
        }


        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void GrainIdShouldEncodeAndDecodePrimaryKeyGuidCorrectly()
        {
            const int repeat = 100;
            for (int i = 0; i < repeat; ++i)
            {
                Guid expected = Guid.NewGuid();
                GrainId grainId = GrainId.GetGrainIdForTesting(expected);
                Guid actual = grainId.Key.PrimaryKeyToGuid();
                Assert.AreEqual(expected, actual, string.Format("Failed to encode and decode grain id with GUID {0}", expected));
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void GrainId_ToFromPrintableString()
        {
            Guid guid = Guid.NewGuid();
            GrainId grainId = GrainId.GetGrainIdForTesting(guid);
            GrainId roundTripped = RoundTripGrainIdToParsable(grainId);
            Assert.AreEqual(grainId, roundTripped, "GrainId.ToPrintableString -- Guid key");

            string extKey = "Guid-ExtKey-1";
            guid = Guid.NewGuid();
            grainId = GrainId.GetGrainId(0, guid, extKey);
            roundTripped = RoundTripGrainIdToParsable(grainId);
            Assert.AreEqual(grainId, roundTripped, "GrainId.ToPrintableString -- Guid key + Extended Key");

            grainId = GrainId.GetGrainId(0, guid, null);
            roundTripped = RoundTripGrainIdToParsable(grainId);
            Assert.AreEqual(grainId, roundTripped, "GrainId.ToPrintableString -- Guid key + null Extended Key");

            long key = random.Next();
            guid = UniqueKey.NewKey(key).PrimaryKeyToGuid();
            grainId = GrainId.GetGrainIdForTesting(guid);
            roundTripped = RoundTripGrainIdToParsable(grainId);
            Assert.AreEqual(grainId, roundTripped, "GrainId.ToPrintableString -- Int64 key");

            extKey = "Long-ExtKey-2";
            key = random.Next();
            guid = UniqueKey.NewKey(key).PrimaryKeyToGuid();
            grainId = GrainId.GetGrainId(0, guid, extKey);
            roundTripped = RoundTripGrainIdToParsable(grainId);
            Assert.AreEqual(grainId, roundTripped, "GrainId.ToPrintableString -- Int64 key + Extended Key");
            
            guid = UniqueKey.NewKey(key).PrimaryKeyToGuid();
            grainId = GrainId.GetGrainId(0, guid, null);
            roundTripped = RoundTripGrainIdToParsable(grainId);
            Assert.AreEqual(grainId, roundTripped, "GrainId.ToPrintableString -- Int64 key + null Extended Key");
        }

        private GrainId RoundTripGrainIdToParsable(GrainId input)
        {
            string str = input.ToParsableString();
            GrainId output = GrainId.FromParsableString(str);
            return output;
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")] 
        public void UniqueTypeCodeDataShouldStore32BitsOfInformation()
        {
            const int expected = unchecked((int)0xfabccbaf);
            var uk = UniqueKey.NewKey(0, UniqueKey.Category.None, expected);
            var actual = uk.BaseTypeCode;

            Assert.AreEqual(
                expected,
                actual,
                "UniqueKey.BaseTypeCode should store at least 32 bits of information.");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void UniqueKeysShouldPreserveTheirPrimaryKeyValueIfItIsGuid()
        {
            const int all32Bits = unchecked((int)0xffffffff);
            var expectedKey1 = Guid.NewGuid();
            const string expectedKeyExt1 = "1";
            var uk1 = UniqueKey.NewKey(expectedKey1, UniqueKey.Category.KeyExtGrain, all32Bits, expectedKeyExt1);
            string actualKeyExt1;
            var actualKey1 = uk1.PrimaryKeyToGuid(out actualKeyExt1);
            Assert.AreEqual(
                expectedKey1,
                actualKey1,
                "UniqueKey objects should preserve the value of their primary key (Guid case #1).");
            Assert.AreEqual(
                expectedKeyExt1,
                actualKeyExt1,
                "UniqueKey objects should preserve the value of their key extension (Guid case #1).");

            var expectedKey2 = Guid.NewGuid();
            const string expectedKeyExt2 = "2";
            var uk2 = UniqueKey.NewKey(expectedKey2, UniqueKey.Category.KeyExtGrain, all32Bits, expectedKeyExt2);
            string actualKeyExt2;
            var actualKey2 = uk2.PrimaryKeyToGuid(out actualKeyExt2);
            Assert.AreEqual(
                expectedKey2,
                actualKey2,
                "UniqueKey objects should preserve the value of their primary key (Guid case #2).");
            Assert.AreEqual(
                expectedKeyExt2,
                actualKeyExt2,
                "UniqueKey objects should preserve the value of their key extension (Guid case #2).");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
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

            Assert.AreEqual(
                expectedKey,
                actualKey,
                "UniqueKey objects should preserve the value of their primary key (long case).");
            Assert.AreEqual(
                expectedKeyExt,
                actualKeyExt,
                "UniqueKey objects should preserve the value of their key extension (long case).");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void ID_HashCorrectness()
        {
            // This tests that our optimized Jenkins hash computes the same value as the reference implementation
            int testCount = 1000;
            JenkinsHash jenkinsHash = JenkinsHash.Factory.GetHashGenerator(false);
            for (int i = 0; i < testCount; i++)
            {
                byte[] byteData = new byte[24];
                random.NextBytes(byteData);
                ulong u1 = BitConverter.ToUInt64(byteData, 0);
                ulong u2 = BitConverter.ToUInt64(byteData, 8);
                ulong u3 = BitConverter.ToUInt64(byteData, 16);
                var referenceHash = jenkinsHash.ComputeHash(byteData);
                var optimizedHash = jenkinsHash.ComputeHash(u1, u2, u3);
                Assert.AreEqual(referenceHash, optimizedHash, "Optimized hash value doesn't match the reference value for inputs {0}, {1}, {2}", u1, u2, u3);
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void ID_Interning_GrainID()
        {
            Guid guid = new Guid();
            GrainId gid1 = GrainId.FromParsableString(guid.ToString("B"));
            GrainId gid2 = GrainId.FromParsableString(guid.ToString("N"));
            Assert.AreEqual(gid1, gid2, "Should be equal GrainId's");
            Assert.AreSame(gid1, gid2, "Should be same / intern'ed GrainId object");

            // Round-trip through Serializer
            GrainId gid3 = (GrainId) SerializationManager.RoundTripSerializationForTesting(gid1);
            Assert.AreEqual(gid1, gid3, "Should be equal GrainId's");
            Assert.AreEqual(gid2, gid3, "Should be equal GrainId's");
            Assert.AreSame(gid1, gid3, "Should be same / intern'ed GrainId object");
            Assert.AreSame(gid2, gid3, "Should be same / intern'ed GrainId object");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void ID_Interning_string_equals()
        {
            Interner<string, string> interner = new Interner<string, string>();
            const string str = "1";
            string r1 = interner.FindOrCreate("1", () => str);
            string r2 = interner.FindOrCreate("1", () => null); // Should always be found

            Assert.AreEqual(r1, r2, "1: Objects should be equal");
            Assert.AreSame(r1, r2, "2: Objects should be same / intern'ed");

            // Round-trip through Serializer
            string r3 = (string) SerializationManager.RoundTripSerializationForTesting(r1);

            Assert.AreEqual(r1, r3, "3: Should be equal");
            Assert.AreEqual(r2, r3, "4: Should be equal");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void ID_Intern_derived_class()
        {
            Interner<int, A> interner = new Interner<int, A>();
            var obj1 = new A();
            var obj2 = new B();
            var obj3 = new B();

            var r1 = interner.InternAndUpdateWithMoreDerived(1, obj1);
            Assert.AreEqual(obj1, r1, "Objects should be equal");
            Assert.AreSame(obj1, r1, "Objects should be same / intern'ed");

            var r2 = interner.InternAndUpdateWithMoreDerived(2, obj2);
            Assert.AreEqual(obj2, r2, "Objects should be equal");
            Assert.AreSame(obj2, r2, "Objects should be same / intern'ed");

            // Interning should not replace instances of same class
            var r3 = interner.InternAndUpdateWithMoreDerived(2, obj3);
            Assert.AreSame(obj2, r3, "Interning should return previous object");
            Assert.AreNotSame(obj3, r3, "Interning should not replace previous object of same class");

            // Interning should return instances of most derived class
            var r4 = interner.InternAndUpdateWithMoreDerived(1, obj2);
            Assert.AreSame(obj2, r4, "Interning should return most derived object");
            Assert.AreNotSame(obj1, r4, "Interning should replace cached instances of less derived object");

            // Interning should not return instances of less derived class
            var r5 = interner.InternAndUpdateWithMoreDerived(2, obj1);
            Assert.AreNotSame(obj1, r5, "Interning should not return less derived object");
            Assert.AreSame(obj2, r5, "Interning should return previously cached instances of more derived object");
        }

       [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void ID_Intern_FindOrCreate_derived_class()
        {
            Interner<int, A> interner = new Interner<int, A>();
            var obj1 = new A();
            var obj2 = new B();
            var obj3 = new B();

            var r1 = interner.FindOrCreate(1, () => obj1);
            Assert.AreEqual(obj1, r1, "Objects should be equal");
            Assert.AreSame(obj1, r1, "Objects should be same / intern'ed");

            var r2 = interner.FindOrCreate(2, () => obj2);
            Assert.AreEqual(obj2, r2, "Objects should be equal");
            Assert.AreSame(obj2, r2, "Objects should be same / intern'ed");

            // FindOrCreate should not replace instances of same class
            var r3 = interner.FindOrCreate(2, () => obj3);
            Assert.AreSame(obj2, r3, "FindOrCreate should return previous object");
            Assert.AreNotSame(obj3, r3, "FindOrCreate should not replace previous object of same class");

            // FindOrCreate should not replace cached instances with instances of most derived class
            var r4 = interner.FindOrCreate(1, () => obj2);
            Assert.AreSame(obj1, r4, "FindOrCreate return previously cached object");
            Assert.AreNotSame(obj2, r4, "FindOrCreate should not replace previously cached object");

            // FindOrCreate should not replace cached instances with instances of less derived class
            var r5 = interner.FindOrCreate(2, () => obj1);
            Assert.AreNotSame(obj1, r5, "FindOrCreate should not replace previously cached object");
            Assert.AreSame(obj2, r5, "FindOrCreate return previously cached object");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void Interning_SiloAddress()
        {
            //string addrStr1 = "1.2.3.4@11111@1";
            SiloAddress a1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1111), 12345);
            SiloAddress a2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1111), 12345);
            Assert.AreEqual(a1, a2, "Should be equal SiloAddress's");
            Assert.AreSame(a1, a2, "Should be same / intern'ed SiloAddress object");

            // Round-trip through Serializer
            SiloAddress a3 = (SiloAddress) SerializationManager.RoundTripSerializationForTesting(a1);
            Assert.AreEqual(a1, a3, "Should be equal SiloAddress's");
            Assert.AreEqual(a2, a3, "Should be equal SiloAddress's");
            Assert.AreSame(a1, a3, "Should be same / intern'ed SiloAddress object");
            Assert.AreSame(a2, a3, "Should be same / intern'ed SiloAddress object");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void Interning_SiloAddress2()
        {
            SiloAddress a1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1111), 12345);
            SiloAddress a2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 2222), 12345);
            Assert.AreNotEqual(a1, a2, "Should not be equal SiloAddress's");
            Assert.AreNotSame(a1, a2, "Should not be same / intern'ed SiloAddress object");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void Interning_SiloAddress_Serialization()
        {
            SiloAddress a1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1111), 12345);

            // Round-trip through Serializer
            SiloAddress a3 = (SiloAddress) SerializationManager.RoundTripSerializationForTesting(a1);
            Assert.AreEqual(a1, a3, "Should be equal SiloAddress's");
            Assert.AreSame(a1, a3, "Should be same / intern'ed SiloAddress object");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void GrainID_AsGuid()
        {
            string guidString = "0699605f-884d-4343-9977-f40a39ab7b2b";
            Guid grainIdGuid = Guid.Parse(guidString);
            GrainId grainId = GrainId.GetGrainIdForTesting(grainIdGuid);
            //string grainIdToKeyString = grainId.ToKeyString();
            string grainIdToFullString = grainId.ToFullString();
            string grainIdToGuidString = GrainIdToGuidString(grainId);
            string grainIdKeyString = grainId.Key.ToString();

            Console.WriteLine("Guid={0}", grainIdGuid);
            Console.WriteLine("GrainId={0}", grainId);
            //Console.WriteLine("GrainId.ToKeyString={0}", grainIdToKeyString);
            Console.WriteLine("GrainId.Key.ToString={0}", grainIdKeyString);
            Console.WriteLine("GrainIdToGuidString={0}", grainIdToGuidString);
            Console.WriteLine("GrainId.ToFullString={0}", grainIdToFullString);

            // Equal: Public APIs
            //Assert.AreEqual(guidString, grainIdToKeyString, "GrainId.ToKeyString");
            Assert.AreEqual(guidString, grainIdToGuidString, "GrainIdToGuidString");
            // Equal: Internal APIs
            Assert.AreEqual(grainIdGuid, grainId.GetPrimaryKey(), "GetPrimaryKey Guid");
            // NOT-Equal: Internal APIs
            Assert.AreNotEqual(guidString, grainIdKeyString, "GrainId.Key.ToString");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers")]
        public void SiloAddress_ToFrom_ParsableString()
        {
            string testName = TestContext.TestName;
            Console.WriteLine(testName);

            SiloAddress address1 = SiloAddress.NewLocalAddress(12345);

            string addressStr1 = address1.ToParsableString();
            SiloAddress addressObj1 = SiloAddress.FromParsableString(addressStr1);

            Console.WriteLine("Convert -- From: {0} Got result string: '{1}' object: {2}",
                address1, addressStr1, addressObj1);

            Assert.AreEqual(address1, addressObj1, "SiloAddress equal after To-From-ParsableString");

            //const string addressStr2 = "127.0.0.1-11111-144611139";
            const string addressStr2 = "127.0.0.1:11111@144611139";
            SiloAddress addressObj2 = SiloAddress.FromParsableString(addressStr2);
            string addressStr2Out = addressObj2.ToParsableString();

            Console.WriteLine("Convert -- From: {0} Got result string: '{1}' object: {2}",
                addressStr2, addressStr2Out, addressObj2);

            Assert.AreEqual(addressStr2, addressStr2Out, "SiloAddress equal after From-To-ParsableString");
        }

        internal string GrainIdToGuidString(GrainId grainId)
        {
            const string pkIdentifierStr = "PrimaryKey:";
            string grainIdFullString = grainId.ToFullString();
            int pkStartIdx = grainIdFullString.IndexOf(pkIdentifierStr, StringComparison.Ordinal) + pkIdentifierStr.Length + 1;
            string pkGuidString = grainIdFullString.Substring(pkStartIdx, Guid.Empty.ToString().Length);
            return pkGuidString;
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Identifiers"), TestCategory("GrainReference")]
        public void GrainReference_Test1()
        {
            Guid guid = Guid.NewGuid();
            GrainId regularGrainId = GrainId.GetGrainIdForTesting(guid);
            GrainReference grainRef = GrainReference.FromGrainId(regularGrainId);
            TestGrainReference(grainRef);

            grainRef = GrainReference.FromGrainId(regularGrainId, "generic");
            TestGrainReference(grainRef);

            GrainId systemTragetGrainId = GrainId.NewSystemTargetGrainIdByTypeCode(2);
            grainRef = GrainReference.FromGrainId(systemTragetGrainId, null, SiloAddress.NewLocalAddress(1));
            TestGrainReference(grainRef);

            GrainId observerGrainId = GrainId.NewClientId();
            grainRef = GrainReference.NewObserverGrainReference(observerGrainId, GuidId.GetNewGuidId());
            TestGrainReference(grainRef);
        }

        private void TestGrainReference(GrainReference grainRef)
        {
            GrainReference roundTripped = RoundTripGrainReferenceToKey(grainRef);
            Assert.AreEqual(grainRef, roundTripped, "GrainReference.ToKeyString");

            roundTripped = SerializationManager.RoundTripSerializationForTesting(grainRef);
            Assert.AreEqual(grainRef, roundTripped, "GrainReference.OrleansSerializer");

            roundTripped = TestingUtils.RoundTripDotNetSerializer(grainRef);
            Assert.AreEqual(grainRef, roundTripped, "GrainReference.DotNetSerializer");
        }

        private GrainReference RoundTripGrainReferenceToKey(GrainReference input)
        {
            string str = input.ToKeyString();
            GrainReference output = GrainReference.FromKeyString(str);
            return output;
        }
    }
}
