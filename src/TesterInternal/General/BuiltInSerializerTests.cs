using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Concurrency;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.CodeGeneration;

// ReSharper disable NotAccessedVariable

namespace UnitTests.SerializerTests
{
    /// <summary>
    /// Test the built-in serializers
    /// </summary>
    [TestClass]
    public class BuiltInSerializerTests
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            TraceLogger.Initialize(new NodeConfiguration());
            SerializationManager.Initialize(false);
            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_ActivationAddress()
        {
            SerializationManager.UseStandardSerializer = false;
            var grain = GrainId.NewId();
            var addr = ActivationAddress.GetAddress(null, grain, null);
            var deserialized = OrleansSerializationLoop(addr, false);
            Assert.IsInstanceOfType(deserialized, typeof(ActivationAddress), "ActivationAddress copied as wrong type");
            Assert.IsNull(((ActivationAddress)deserialized).Activation, "Activation no longer null after copy");
            Assert.IsNull(((ActivationAddress)deserialized).Silo, "Silo no longer null after copy");
            Assert.AreEqual(grain, ((ActivationAddress)deserialized).Grain, "Grain different after copy");
            deserialized = OrleansSerializationLoop(addr);
            Assert.IsInstanceOfType(deserialized, typeof(ActivationAddress), "ActivationAddress full serialization loop as wrong type");
            Assert.IsNull(((ActivationAddress)deserialized).Activation, "Activation no longer null after full serialization loop");
            Assert.IsNull(((ActivationAddress)deserialized).Silo, "Silo no longer null after full serialization loop");
            Assert.AreEqual(grain, ((ActivationAddress)deserialized).Grain, "Grain different after copy");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_EmptyList()
        {
            SerializationManager.UseStandardSerializer = false;
            var list = new List<int>();
            var deserialized = OrleansSerializationLoop(list, false);
            Assert.IsInstanceOfType(deserialized, typeof (List<int>), "Empty list of integers copied as wrong type");
            ValidateList(list, (List<int>)deserialized, "int (empty, copy)");
            deserialized = OrleansSerializationLoop(list);
            Assert.IsInstanceOfType(deserialized, typeof(List<int>), "Empty list of integers full serialization loop as wrong type");
            ValidateList(list, (List<int>)deserialized, "int (empty)");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_BasicDictionaries()
        {
            SerializationManager.UseStandardSerializer = false;

            Dictionary<string, string> source1 = new Dictionary<string, string>();
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            var deserialized = OrleansSerializationLoop(source1);
            ValidateDictionary<string, string>(source1, deserialized, "string/string");

            Dictionary<int, DateTime> source2 = new Dictionary<int, DateTime>();
            source2[3] = DateTime.Now;
            source2[27] = DateTime.Now.AddHours(2);
            deserialized = OrleansSerializationLoop(source2);
            ValidateDictionary<int, DateTime>(source2, deserialized, "int/date");
        }

        [Serializable]
        public class CaseInsensitiveStringEquality : EqualityComparer<string>
        {
            public override bool Equals(string x, string y)
            {
                return x.Equals(y, StringComparison.OrdinalIgnoreCase);
            }

            public override int GetHashCode(string obj)
            {
                return obj.ToLowerInvariant().GetHashCode();
            }
        }

        [Serializable]
        public class Mod5IntegerComparer : EqualityComparer<int>
        {
            public override bool Equals(int x, int y)
            {
                return ((x - y) % 5) == 0;
            }

            public override int GetHashCode(int obj)
            {
                return obj % 5;
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_DictionaryWithComparer()
        {
            SerializationManager.UseStandardSerializer = false;

            Dictionary<string, string> source1 = new Dictionary<string, string>(new CaseInsensitiveStringEquality());
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            var deserialized = OrleansSerializationLoop(source1);
            ValidateDictionary<string, string>(source1, deserialized, "case-insensitive string/string");
            Dictionary<string, string> result1 = deserialized as Dictionary<string, string>;
            Assert.AreEqual<string>(source1["Hello"], result1["hElLo"], "Round trip for case insensitive string/string dictionary lost the custom comparer");

            Dictionary<int, DateTime> source2 = new Dictionary<int, DateTime>(new Mod5IntegerComparer());
            source2[3] = DateTime.Now;
            source2[27] = DateTime.Now.AddHours(2);
            deserialized = OrleansSerializationLoop(source2);
            ValidateDictionary<int, DateTime>(source2, deserialized, "int/date");
            Dictionary<int, DateTime> result2 = (Dictionary<int, DateTime>)deserialized;
            Assert.AreEqual<DateTime>(source2[3], result2[13], "Round trip for case insensitive int/DateTime dictionary lost the custom comparer");
        }

        [Serializable]
        class CaseInsensitiveStringComparer : Comparer<string>
        {
            public override int Compare(string x, string y)
            {
                var x1 = x.ToLowerInvariant();
                var y1 = y.ToLowerInvariant();
                return Comparer<string>.Default.Compare(x1, y1);
            }
        }

        public enum IntEnum
        {
            Value1,
            Value2,
            Value3
        }

        public enum UShortEnum : ushort
        {
            Value1,
            Value2,
            Value3
        }

        public enum CampaignEnemyType : sbyte
        {
            None = -1,
            Brute = 0,
            Enemy1,
            Enemy2,
            Enemy3,
            Enemy4,
        }


        /*[TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_Enums()
        {
            SerializationManager.UseStandardSerializer = false;

            var result = OrleansSerializationLoop(IntEnum.Value2);
            Assert.IsInstanceOfType(result, typeof(IntEnum), "Serialization round-trip resulted in incorrect type, " + result.GetType().Name + ", for int enum");
            Assert.AreEqual(IntEnum.Value2, (IntEnum)result, "Serialization round-trip resulted in incorrect value for int enum");

            var result2 = OrleansSerializationLoop(UShortEnum.Value3);
            Assert.IsInstanceOfType(result2, typeof(UShortEnum), "Serialization round-trip resulted in incorrect type, " + result2.GetType().Name + ", for ushort enum");
            Assert.AreEqual(UShortEnum.Value3, (UShortEnum)result2, "Serialization round-trip resulted in incorrect value for ushort enum");

            var test = new ClassWithEnumTestData { EnumValue = TestEnum.Third, Enemy = CampaignEnemyTestType.Enemy3 };
            var result3 = OrleansSerializationLoop(test);
            Assert.IsInstanceOfType(result3, typeof(ClassWithEnumTestData), "Serialization round-trip resulted in incorrect type, " + result3.GetType().Name +
                ", for enum-containing class");
            var r3 = (ClassWithEnumTestData) result3;
            Assert.AreEqual(TestEnum.Third, r3.EnumValue, "Serialization round-trip resulted in incorrect value for enum-containing class (Third)");
            Assert.AreEqual(CampaignEnemyTestType.Enemy3, r3.Enemy, "Serialization round-trip resulted in incorrect value for enum-containing class (Enemy)");

            var result4 = OrleansSerializationLoop(CampaignEnemyType.Enemy3);
            Assert.IsInstanceOfType(result4, typeof(CampaignEnemyType), "Serialization round-trip resulted in incorrect type, " + result4.GetType().Name + ", for sbyte enum");
            Assert.AreEqual(CampaignEnemyType.Enemy3, (CampaignEnemyType)result4, "Serialization round-trip resulted in incorrect value for sbyte enum");
        }*/

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_SortedDictionaryWithComparer()
        {
            SerializationManager.UseStandardSerializer = false;

            var source1 = new SortedDictionary<string, string>(new CaseInsensitiveStringComparer());
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            object deserialized = OrleansSerializationLoop(source1);
            ValidateSortedDictionary<string, string>(source1, deserialized, "string/string");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_SortedListWithComparer()
        {
            SerializationManager.UseStandardSerializer = false;

            var source1 = new SortedList<string, string>(new CaseInsensitiveStringComparer());
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            object deserialized = OrleansSerializationLoop(source1);
            ValidateSortedList<string, string>(source1, deserialized, "string/string");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_HashSetWithComparer()
        {
            SerializationManager.UseStandardSerializer = false;

            var source1 = new HashSet<string>(new CaseInsensitiveStringEquality());
            source1.Add("one");
            source1.Add("two");
            source1.Add("three");
            var deserialized = OrleansSerializationLoop(source1);
            Assert.IsInstanceOfType(deserialized, source1.GetType(), "Type is wrong after round-trip of string hash set with comparer");
            var result = deserialized as HashSet<string>;
            Assert.AreEqual(source1.Count, result.Count, "Count is wrong after round-trip of string hash set with comparer");
            foreach (var key in source1)
            {
                Assert.IsTrue(result.Contains(key), "Key " + key + " is missing after round-trip of string hash set with comparer");
            }
            Assert.IsTrue(result.Contains("One"), "Comparer is wrong after round-trip of string hash set with comparer");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_Stack()
        {
            SerializationManager.UseStandardSerializer = false;

            var source1 = new Stack<string>();
            source1.Push("one");
            source1.Push("two");
            source1.Push("three");
            object deserialized = OrleansSerializationLoop(source1);
            Assert.IsInstanceOfType(deserialized, source1.GetType(), "Type is wrong after round-trip of string stack");
            var result = deserialized as Stack<string>;
            Assert.AreEqual(source1.Count, result.Count, "Count is wrong after round-trip of string stack");

            var srcIter = source1.GetEnumerator();
            var resIter = result.GetEnumerator();
            while (srcIter.MoveNext() && resIter.MoveNext())
            {
                Assert.AreEqual<string>(srcIter.Current, resIter.Current, "Data is wrong after round-trip of string stack");
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_SortedSetWithComparer()
        {
            SerializationManager.UseStandardSerializer = false;

            var source1 = new SortedSet<string>(new CaseInsensitiveStringComparer());
            source1.Add("one");
            source1.Add("two");
            source1.Add("three");
            object deserialized = OrleansSerializationLoop(source1);
            Assert.IsInstanceOfType(deserialized, source1.GetType(), "Type is wrong after round-trip of string sorted set with comparer");
            var result = (SortedSet<string>)deserialized;
            Assert.AreEqual(source1.Count, result.Count, "Count is wrong after round-trip of string sorted set with comparer");
            foreach (var key in source1)
            {
                Assert.IsTrue(result.Contains(key), "Key " + key + " is missing after round-trip of string sorted set with comparer");
            }
            Assert.IsTrue(result.Contains("One"), "Comparer is wrong after round-trip of string sorted set with comparer");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_Array()
        {
            SerializationManager.UseStandardSerializer = false;

            var source1 = new int[] { 1, 3, 5 };
            object deserialized = OrleansSerializationLoop(source1);
            ValidateArray<int>(source1, deserialized, "int");

            var source2 = new string[] { "hello", "goodbye", "yes", "no", "", "I don't know" };
            deserialized = OrleansSerializationLoop(source2);
            ValidateArray<string>(source2, deserialized, "string");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_ArrayOfArrays()
        {
            SerializationManager.UseStandardSerializer = false;

            var source1 = new[] { new[] { 1, 3, 5 }, new[] { 10, 20, 30 }, new[] { 17, 13, 11, 7, 5, 3, 2 } };
            object deserialized = OrleansSerializationLoop(source1);
            ValidateArrayOfArrays(source1, deserialized, "int");

            var source2 = new[] { new[] { "hello", "goodbye", "yes", "no", "", "I don't know" }, new[] { "yes" } };
            deserialized = OrleansSerializationLoop(source2);
            ValidateArrayOfArrays(source2, deserialized, "string");

            var source3 = new HashSet<string>[3][];
            source3[0] = new HashSet<string>[2];
            source3[1] = new HashSet<string>[3];
            source3[2] = new HashSet<string>[1];
            source3[0][0] = new HashSet<string>();
            source3[0][1] = new HashSet<string>();
            source3[1][0] = new HashSet<string>();
            source3[1][1] = null;
            source3[1][2] = new HashSet<string>();
            source3[2][0] = new HashSet<string>();
            source3[0][0].Add("this");
            source3[0][0].Add("that");
            source3[1][0].Add("the other");
            source3[1][2].Add("and another");
            source3[2][0].Add("but not yet another");
            deserialized = OrleansSerializationLoop(source3);
            Assert.IsInstanceOfType(deserialized, typeof(HashSet<string>[][]), "Array of arrays of hash sets type is wrong on deserialization");
            var result = (HashSet<string>[][])deserialized;
            Assert.AreEqual(3, result.Length, "Outer array size wrong on array of array of sets");
            Assert.AreEqual(2, result[0][0].Count, "Inner set size wrong on array of array of sets, element 0,0");
            Assert.AreEqual(0, result[0][1].Count, "Inner set size wrong on array of array of sets, element 0,1");
            Assert.AreEqual(1, result[1][0].Count, "Inner set size wrong on array of array of sets, element 1,0");
            Assert.IsNull(result[1][1], "Inner set not null on array of array of sets, element 1, 1");
            Assert.AreEqual(1, result[1][2].Count, "Inner set size wrong on array of array of sets, element 1,2");
            Assert.AreEqual(1, result[2][0].Count, "Inner set size wrong on array of array of sets, element 2,0");

            var source4 = new GrainReference[3][];
            source4[0] = new GrainReference[2];
            source4[1] = new GrainReference[3];
            source4[2] = new GrainReference[1];
            source4[0][0] = GrainReference.FromGrainId(GrainId.NewId());
            source4[0][1] = GrainReference.FromGrainId(GrainId.NewId());
            source4[1][0] = GrainReference.FromGrainId(GrainId.NewId());
            source4[1][1] = GrainReference.FromGrainId(GrainId.NewId());
            source4[1][2] = GrainReference.FromGrainId(GrainId.NewId());
            source4[2][0] = GrainReference.FromGrainId(GrainId.NewId());
            deserialized = OrleansSerializationLoop(source4);
            ValidateArrayOfArrays(source4, deserialized, "grain reference");

            var source5 = new GrainReference[32][];
            for (int i = 0; i < source5.Length; i++)
            {
                source5[i] = new GrainReference[64];
                for (int j = 0; j < source5[i].Length; j++)
                {
                    source5[i][j] = GrainReference.FromGrainId(GrainId.NewId());
                }
            }
            deserialized = OrleansSerializationLoop(source5);
            ValidateArrayOfArrays(source5, deserialized, "grain reference (large)");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_ArrayOfArrayOfArrays()
        {
            SerializationManager.UseStandardSerializer = false;

            var source1 = new[] {new[] {1, 3, 5}, new[] {10, 20, 30}, new[] {17, 13, 11, 7, 5, 3, 2}};
            var source2 = new[] { new[] { 1, 3 }, new[] { 10, 20 }, new[] { 17, 13, 11, 7, 5 } };
            var source3 = new[] { new[] { 1, 3, 5 }, new[] { 10, 20, 30 } };
            var source = new[] {source1, source2, source3};
            object deserialized = OrleansSerializationLoop(source);
            ValidateArrayOfArrayOfArrays(source, deserialized, "int");
        }

        public class UnserializableException : Exception
        {
            public UnserializableException(string message) : base(message)
            {}

            [CopierMethod]
            static private object Copy(object input)
            {
                return input;
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_UnserializableException()
        {
            SerializationManager.UseStandardSerializer = false;

            const string message = "This is a test message";
            var source = new UnserializableException(message);
            object deserialized = OrleansSerializationLoop(source);
            Assert.IsInstanceOfType(deserialized, typeof(Exception), "Type is wrong after round trip of unserializable exception");
            var result = (Exception)deserialized;
            var expectedMessage = "Non-serializable exception of type " +
                                  typeof(UnserializableException).OrleansTypeName() + ": " + message;
            Assert.IsTrue(result.Message.StartsWith(expectedMessage), "Exception message is wrong after round trip of unserializable exception");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_ObjectIdentity()
        {
            var val = new List<string> {"first", "second"};

            var val2 = new List<string> {"first", "second"};

            var source = new Dictionary<string, List<string>>();
            source["one"] = val;
            source["two"] = val;
            source["three"] = val2;
            Assert.IsTrue(object.ReferenceEquals(source["one"], source["two"]), "Object identity lost before round trip of string/list dict!!!");

            var deserialized = OrleansSerializationLoop(source);
            Assert.IsInstanceOfType(deserialized, typeof(Dictionary<string, List<string>>), "Type is wrong after round-trip of string/list dict");
            var result = (Dictionary<string, List<string>>)deserialized;
            Assert.AreEqual(source.Count, result.Count, "Count is wrong after round-trip of string/list dict");

            List<string> list1;
            List<string> list2;
            List<string> list3;
            Assert.IsTrue(result.TryGetValue("one", out list1), "Key 'one' not found after round trip of string/list dict");
            Assert.IsTrue(result.TryGetValue("two", out list2), "Key 'two' not found after round trip of string/list dict");
            Assert.IsTrue(result.TryGetValue("three", out list3), "Key 'three' not found after round trip of string/list dict");

            ValidateList<string>(val, list1, "string");
            ValidateList<string>(val, list2, "string");
            ValidateList<string>(val2, list3, "string");

            Assert.IsTrue(object.ReferenceEquals(list1, list2), "Object identity lost after round trip of string/list dict");
            Assert.IsFalse(object.ReferenceEquals(list2, list3), "Object identity gained after round trip of string/list dict");
            Assert.IsFalse(object.ReferenceEquals(list1, list3), "Object identity gained after round trip of string/list dict");
        }

        [Serializable]
        public class Unrecognized
        {
            public int A { get; set; }
            public int B { get; set; }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_Unrecognized()
        {
            var test1 = new Unrecognized { A = 3, B = 27 };
            var raw = OrleansSerializationLoop(test1, false);
            Assert.IsInstanceOfType(raw, typeof(Unrecognized), "Type is wrong after deep copy of unrecognized");
            var result = (Unrecognized)raw;
            Assert.AreEqual(3, result.A, "Property A is wrong after deep copy of unrecognized");
            Assert.AreEqual(27, result.B, "Property B is wrong after deep copy of unrecognized");

            var test2 = new Unrecognized[3];
            for (int i = 0; i < 3; i++)
            {
                test2[i] = new Unrecognized { A = i, B = 2 * i };
            }
            raw = OrleansSerializationLoop(test2);
            Assert.IsInstanceOfType(raw, typeof(Unrecognized[]), "Type is wrong after round trip of array of unrecognized");
            var result2 = (Unrecognized[])raw;
            Assert.AreEqual(3, result2.Length, "Array length is wrong after round trip of array of unrecognized");
            for (int j = 0; j < 3; j++)
            {
                Assert.AreEqual(j, result2[j].A, "Property A at index " + j + "is wrong after round trip of array of unrecognized");
                Assert.AreEqual(2 * j, result2[j].B, "Property B at index " + j + "is wrong after round trip of array of unrecognized");
            }
        }
/*
        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_Immutable()
        {
            var test1 = new ImmutableType(3, 27);
            var raw = SerializationManager.DeepCopy(test1);
            Assert.IsInstanceOfType(raw, typeof(ImmutableType), "Type is wrong after deep copy of [Immutable] type");
            Assert.AreSame(test1, raw, "Deep copy of [Immutable] object made a copy instead of just copying the pointer");

            var test2list = new List<int>();
            for (int i = 0; i < 3; i++)
            {
                test2list.Add(i);
            }
            var test2 = new Immutable<List<int>>(test2list);
            raw = SerializationManager.DeepCopy(test2);
            Assert.IsInstanceOfType(raw, typeof(Immutable<List<int>>), "Type is wrong after round trip of array of Immutable<>");
            Assert.AreSame(test2.Value, ((Immutable<List<int>>)raw).Value, "Deep copy of Immutable<> object made a copy instead of just copying the pointer");

            var test3 = new EmbeddedImmutable("test", 1, 2, 3, 4);
            raw = SerializationManager.DeepCopy(test3);
            Assert.IsInstanceOfType(raw, typeof(EmbeddedImmutable), "Type is wrong after deep copy of type containing an Immutable<> field");
            Assert.AreSame(test3.B.Value, ((EmbeddedImmutable)raw).B.Value, "Deep copy of embedded [Immutable] object made a copy instead of just copying the pointer");
        }*/

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_Uri_Multithreaded()
        {
            Parallel.For(0, 50, i =>
            {
                Uri test1 = new Uri("http://www.microsoft.com/" + i);
                object raw = SerializationManager.DeepCopy(test1);
                Assert.IsInstanceOfType(raw, typeof(Uri), "Type is wrong after deep copy of Uri");
                Assert.AreSame(test1, raw, "Deep copy made a copy instead of just copying the pointer");

                object deserialized = OrleansSerializationLoop(test1);
                Assert.IsInstanceOfType(deserialized, typeof(Uri), "Type is wrong after round-trip of Uri");
                Uri result = (Uri)deserialized;
                Assert.AreEqual(test1, result, "Wrong contents after round-trip of Uri");
            });
        }

        ////[TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        //public void Serialize_RequestInvocationHistory()
        //{
        //    //SerializationManager.UseStandardSerializer = false;

        //    //Message inMsg = new Message();
        //    //inMsg.TargetGrain = GrainId.NewId();
        //    //inMsg.TargetActivation = ActivationId.NewId();
        //    //inMsg.InterfaceId = 12;
        //    //inMsg.MethodId = 13;

        //    //RequestInvocationHistory src = new RequestInvocationHistory(inMsg);
        //    //inMsg.AddToCallChainHeader(src);

        //    ////object deserialized = OrleansSerializationLoop(inMsg);
        //    ////Message outMsg = (Message)deserialized;
        //    ////IEnumerable<RequestInvocationHistory> dstArray = outMsg.CallChainHeader;
        //    ////RequestInvocationHistory dst = dstArray.FirstOrDefault();

        //    ////object deserialized = OrleansSerializationLoop(src);
        //    ////RequestInvocationHistory dst = (RequestInvocationHistory)deserialized;

        //    //Dictionary<string, object> deserialized = SerializeMessage(inMsg);
        //    //IEnumerable<RequestInvocationHistory> dstArray = ((IEnumerable)deserialized[Message.Header.CallChainHeader]).Cast<RequestInvocationHistory>();
        //    //RequestInvocationHistory dst = dstArray.FirstOrDefault();

        //    //Assert.AreEqual(src.GrainId, dst.GrainId);
        //    //Assert.AreEqual(src.ActivationId, dst.ActivationId);
        //    //Assert.AreEqual(src.InterfaceId, dst.InterfaceId);
        //    //Assert.AreEqual(src.MethodId, dst.MethodId);
        //}

        //private Dictionary<string, object> SerializeMessage(Message msg)
        //{
        //    var outStream = new BinaryTokenStreamWriter();
        //    SerializationManager.SerializeMessageHeaders(msg.headers, outStream);
        //    var inStream = new BinaryTokenStreamReader(outStream.ToByteArray());
        //    var copy = SerializationManager.DeserializeMessageHeaders(inStream);
        //    return copy;
        //}

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_GrainReference()
        {
            GrainId grainId = GrainId.NewId();
            GrainReference input = GrainReference.FromGrainId(grainId);
            GrainReference grainRef;

            object deserialized = OrleansSerializationLoop(input);

            Assert.IsInstanceOfType(deserialized, typeof(GrainReference), "GrainReference copied as wrong type");
            grainRef = (GrainReference) deserialized;
            Assert.AreEqual(grainId, grainRef.GrainId, "GrainId different after copy");
            Assert.AreEqual(grainId.GetPrimaryKey(), grainRef.GrainId.GetPrimaryKey(), "PK different after copy");
            Assert.AreEqual(input, grainRef, "Wrong contents after round-trip of " + input);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_GrainReference_ViaStandardSerializer()
        {
            GrainId grainId = GrainId.NewId();
            GrainReference input = GrainReference.FromGrainId(grainId);
            GrainReference grainRef;

            object deserialized = DotNetSerializationLoop(input);

            Assert.IsInstanceOfType(deserialized, typeof(GrainReference), "GrainReference copied as wrong type");
            grainRef = (GrainReference) deserialized;
            Assert.AreEqual(grainId, grainRef.GrainId, "GrainId different after copy");
            Assert.AreEqual(grainId.GetPrimaryKey(), grainRef.GrainId.GetPrimaryKey(), "PK different after copy");
            Assert.AreEqual(input, grainRef, "Wrong contents after round-trip of " + input);
        }

       /* [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_GrainBase()
        {
            Grain input = new EchoTaskGrain();

            object deserialized;

            try
            {
                // Expected exception:
                // OrleansException: No copier found for object of type EchoTaskGrain. Perhaps you need to mark it [Serializable]?

                // ReSharper disable once RedundantAssignment
                deserialized = OrleansSerializationLoop(input);

                Assert.Fail("Should not be able to serialize grain class");
            }
            catch (OrleansException exc)
            {
                if (!exc.Message.Contains("No copier found for object of type"))
                    throw;
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_GrainBase_ViaStandardSerializer()
        {
            Grain input = new EchoTaskGrain();

            object deserialized;

            try
            {
                // Expected exception:
                // System.Runtime.Serialization.SerializationException: Type 'Echo.Grains.EchoTaskGrain' in Assembly 'UnitTestGrains, Version=1.0.0.0, Culture=neutral, PublicKeyToken=070f47935e3ed133' is not marked as serializable.
                
                // ReSharper disable once RedundantAssignment
                deserialized = DotNetSerializationLoop(input);

                Assert.Fail("Should not be able to serialize grain class");
            }
            catch (SerializationException exc)
            {
                if (!exc.Message.Contains("is not marked as serializable"))
                    throw;
            }
        }*/

        private static int staticFilterValue1 = 41;
        private static int staticFilterValue2 = 42;
        private static int staticFilterValue3 = 43;
        private static int staticFilterValue4 = 44;

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_Func()
        {
            int instanceFilterValue2 = staticFilterValue2;

            Func<int, bool> staticFilterFunc = i => i == staticFilterValue3;
            Func<int, bool> instanceFilterFunc = i => i == instanceFilterValue2;

            var serCls = new FuncClass_Serializable();
            Func<int, bool> instanceFuncInSerializableClass = serCls.PredFunc;
            Func<int, bool> staticFuncInSerializableClass = FuncClass_Serializable.StaticPredFunc;
            var nonSerCls = new FuncClass_NotSerializable();
            Func<int, bool> instanceFuncInNonSerializableClass = nonSerCls.PredFunc;
            Func<int, bool> staticFuncInNonSerializableClass = FuncClass_NotSerializable.StaticPredFunc;

            // Works OK
            TestSerializeFuncPtr("Func Lambda - Static field", staticFilterFunc);
            TestSerializeFuncPtr("Static Func In Non Serializable Class", staticFuncInNonSerializableClass);
            TestSerializeFuncPtr("Static Func In Serializable Class", staticFuncInSerializableClass);
            TestSerializeFuncPtr("Func In Serializable Class", instanceFuncInSerializableClass);

            // Fails
            try
            {
                TestSerializeFuncPtr("Func In Non Serializable Class", instanceFuncInNonSerializableClass);
            }
            catch (SerializationException exc)
            {
                Console.WriteLine(exc);
            }
            try
            {
                TestSerializeFuncPtr("Func Lambda - Instance field", instanceFilterFunc);
            }
            catch (SerializationException exc)
            {
                Console.WriteLine(exc);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_Predicate()
        {
            int instanceFilterValue2 = staticFilterValue2;

            Predicate<int> staticPredicate = i => i == staticFilterValue2;
            Predicate<int> instancePredicate = i => i == instanceFilterValue2;
            
            // Works OK
            TestSerializePredicate("Predicate Lambda - Static field", staticPredicate);

            // Fails
            try
            {
                TestSerializePredicate("Predicate Lambda - Instance field", instancePredicate);
            }
            catch (SerializationException exc)
            {
                Console.WriteLine(exc);
            }
        }

        //private static int staticNum;

        //[TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        //public void Serialize_Expression()
        //{
        //    int instanceFilterValue4 = staticFilterValue4;
        //    int instanceNum = 0;

        //    Expression staticExpression = Expression.Condition(
        //        Expression.Constant(staticNum == staticFilterValue4),
        //        Expression.Constant(true),
        //        Expression.Constant(false),
        //        typeof(bool));
        //    Expression instanceExpression = Expression.Condition(
        //        Expression.Constant(instanceNum == instanceFilterValue4),
        //        Expression.Constant(true),
        //        Expression.Constant(false),
        //        typeof(bool));

        //    LambdaExpression staticExpressionLambda = Expression.Lambda<Func<bool>>(staticExpression);
        //    LambdaExpression instanceExpressionLambda = Expression.Lambda<Func<bool>>(instanceExpression);

        //    TestExpression(Expression.Lambda(typeof(bool), staticNum == staticFilterValue4));

        //    // Works OK
        //    TestSerializeExpression("Expression - Static fields", staticExpression);
        //    TestSerializeExpression("Expression - Static Lambda", staticExpressionLambda);

        //    // Fails
        //    TestSerializeExpression("Expression - Instance fields", instanceExpression);
        //    TestSerializeExpression("Expression - Instance Lambda", instanceExpressionLambda);
        //}

        //private void TestExpression(Expression expr)
        //{
        //}

        //private void TestSerializeExpression(string what, Expression expr1)
        //{
        //    object obj2 = OrleansSerializationLoop(expr1);
        //    var expr2 = (Expression) obj2;

        //    foreach (int val in new[] { staticFilterValue1, staticFilterValue2, staticFilterValue3, staticFilterValue4 })
        //    {
        //        Console.WriteLine("{0} -- Compare value={1}", what, val);
        //        Assert.AreEqual(Expression..ev expr1(), expr1(val), "{0} -- Wrong predicate after round-trip of {1} with value={2}", what, pred1, val);
        //    }
        //}

        private void TestSerializeFuncPtr(string what, Func<int, bool> func1)
        {
            object obj2 = OrleansSerializationLoop(func1);
            Func<int, bool> func2 = (Func<int, bool>) obj2;

            foreach (int val in new[] {staticFilterValue1, staticFilterValue2, staticFilterValue3, staticFilterValue4})
            {
                Console.WriteLine("{0} -- Compare value={1}", what, val);
                Assert.AreEqual(func1(val), func2(val), "{0} -- Wrong function after round-trip of {1} with value={2}", what, func1, val);
            }
        }

        private void TestSerializePredicate(string what, Predicate<int> pred1)
        {
            object obj2 = OrleansSerializationLoop(pred1);
            Predicate<int> pred2 = (Predicate<int>) obj2;

            foreach (int val in new[] { staticFilterValue1, staticFilterValue2, staticFilterValue3, staticFilterValue4 })
            {
                Console.WriteLine("{0} -- Compare value={1}", what, val);
                Assert.AreEqual(pred1(val), pred2(val), "{0} -- Wrong predicate after round-trip of {1} with value={2}", what, pred1, val);
            }
        }

        [Serializable]
        internal class FuncClass_Serializable
        {
            public bool PredFunc(int i)
            {
                return i == staticFilterValue2;
            }

            public static bool StaticPredFunc(int i)
            {
                return i == staticFilterValue2;
            }
        }

        internal class FuncClass_NotSerializable
        {
            public bool PredFunc(int i)
            {
                return i == staticFilterValue2;
            }

            public static bool StaticPredFunc(int i)
            {
                int filterValue2 = 42;
                return i == filterValue2;
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void ValidateBuildSegmentListWithLengthLimit()
        {
            byte[] array1 = { 1 };
            byte[] array2 = { 2, 3 };
            byte[] array3 = { 4, 5, 6 };
            byte[] array4 = { 7, 8, 9, 10 };

            List<ArraySegment<byte>> underTest = new List<ArraySegment<byte>>();
            underTest.Add(new ArraySegment<byte>(array1));
            underTest.Add(new ArraySegment<byte>(array2));
            underTest.Add(new ArraySegment<byte>(array3));
            underTest.Add(new ArraySegment<byte>(array4));

            List<ArraySegment<byte>> actual1 = ByteArrayBuilder.BuildSegmentListWithLengthLimit(underTest, 0, 2);
            List<ArraySegment<byte>> actual2 = ByteArrayBuilder.BuildSegmentListWithLengthLimit(underTest, 2, 2);
            List<ArraySegment<byte>> actual3 = ByteArrayBuilder.BuildSegmentListWithLengthLimit(underTest, 4, 2);
            List<ArraySegment<byte>> actual4 = ByteArrayBuilder.BuildSegmentListWithLengthLimit(underTest, 6, 2);
            List<ArraySegment<byte>> actual5 = ByteArrayBuilder.BuildSegmentListWithLengthLimit(underTest, 8, 2);

            // 1: {[1}, {2], 3}
            Assert.AreEqual(0, actual1[0].Offset);
            Assert.AreEqual(1, actual1[0].Count);
            Assert.AreEqual(array1, actual1[0].Array);
            Assert.AreEqual(0, actual1[1].Offset);
            Assert.AreEqual(1, actual1[1].Count);
            Assert.AreEqual(array2, actual1[1].Array);
            // 2: {2, [3}, {4], 5, 6}
            Assert.AreEqual(1, actual2[0].Offset);
            Assert.AreEqual(1, actual2[0].Count);
            Assert.AreEqual(array2, actual2[0].Array);
            Assert.AreEqual(0, actual2[1].Offset);
            Assert.AreEqual(1, actual2[1].Count);
            Assert.AreEqual(array3, actual2[1].Array);
            // 3: {4, [5, 6]}
            Assert.AreEqual(1, actual3[0].Offset);
            Assert.AreEqual(2, actual3[0].Count);
            Assert.AreEqual(array3, actual3[0].Array);
            // 4: {[7, 8], 9, 10}
            Assert.AreEqual(0, actual4[0].Offset);
            Assert.AreEqual(2, actual4[0].Count);
            Assert.AreEqual(array4, actual4[0].Array);
            // 5: {7, 8, [9, 10]}
            Assert.AreEqual(2, actual5[0].Offset);
            Assert.AreEqual(2, actual5[0].Count);
            Assert.AreEqual(array4, actual5[0].Array);
        }

        internal bool AreByteArraysAreEqual(byte[] array1, byte[] array2)
        {
            if (array1.Length != array2.Length)
                return false;

            for (int i = 0; i < array1.Length; i++)
            {
                if (array1[i] != array2[i])
                    return false;
            }

            return true;
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_ListOfMessages_NoError()
        {
            MessagingStatisticsGroup.Init(false);
            InitializeForTesting();
            List<Message> mlist = new List<Message>();
            bool ret;

            for (int i = 0; i < 5; i++)
            {
                Message msg = new Message();
                msg.SendingSilo = SiloAddress.New(new IPEndPoint(i, 3000 + i), 0);
                msg.TargetSilo = SiloAddress.New(new IPEndPoint(10 + i, 3010 + i), 0);

                string str = "";
                for (int j = 1; j <= i; j++)
                {
                    str += "Hello this is just a random message";
                }
                msg.BodyObject = str;

                mlist.Add(msg);
            }
            List<ArraySegment<byte>> serialized;
            int headerLengthOut;
            ret = OutgoingMessageSender.SerializeMessages(mlist, out serialized, out headerLengthOut, null);

            // Message.Serialize(bool) is guranteed to correct by other tests, so we just check the MetaHeader and Lengths
            Assert.AreEqual(true, ret);
            Assert.IsTrue(AreByteArraysAreEqual(serialized[0].Array, new byte[4] { 5, 0, 0, 0 }));
            ////Assert.IsTrue(AreByteArraysAreEqual(serialized[1].Array, new byte[4] { (byte)mlist[0].headerLength, 0, 0, 0 }));
            ////Assert.IsTrue(AreByteArraysAreEqual(serialized[2].Array, new byte[4] { (byte)mlist[0].bodyLength, 0, 0, 0 }));
            ////Assert.IsTrue(AreByteArraysAreEqual(serialized[3].Array, new byte[4] { (byte)mlist[1].headerLength, 0, 0, 0 }));
            ////Assert.IsTrue(AreByteArraysAreEqual(serialized[4].Array, new byte[4] { (byte)mlist[1].bodyLength, 0, 0, 0 }));
            ////Assert.IsTrue(AreByteArraysAreEqual(serialized[5].Array, new byte[4] { (byte)mlist[2].headerLength, 0, 0, 0 }));
            ////Assert.IsTrue(AreByteArraysAreEqual(serialized[6].Array, new byte[4] { (byte)mlist[2].bodyLength, 0, 0, 0 }));
            ////Assert.IsTrue(AreByteArraysAreEqual(serialized[7].Array, new byte[4] { (byte)mlist[3].headerLength, 0, 0, 0 }));
            ////Assert.IsTrue(AreByteArraysAreEqual(serialized[8].Array, new byte[4] { (byte)mlist[3].bodyLength, 0, 0, 0 }));
            ////Assert.IsTrue(AreByteArraysAreEqual(serialized[9].Array, new byte[4] { (byte)mlist[4].headerLength, 0, 0, 0 }));
            ////Assert.IsTrue(AreByteArraysAreEqual(serialized[10].Array, new byte[4] { (byte)mlist[4].bodyLength, 0, 0, 0 }));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_ListOfMessages_WithError()
        {
            MessagingStatisticsGroup.Init(false);
            InitializeForTesting();
            List<Message> mlist = new List<Message>();
            bool ret;

            for (int i = 0; i < 5; i++)
            {
                if (i != 3)
                {
                    Message msg = new Message();
                    msg.SendingSilo = SiloAddress.New(new IPEndPoint(i, 3000 + i), 0);
                    msg.TargetSilo = SiloAddress.New(new IPEndPoint(10 + i, 3010 + i), 0);

                    string str = "";
                    for (int j = 1; j <= i; j++)
                    {
                        str += "Hello this is just a random message";
                    }
                    msg.BodyObject = str;

                    mlist.Add(msg);
                }
                // The 3rd message is invalid, and will cause an exception on Serialization
                else
                {
                    Object unserializableMessage = new Object();
                    mlist.Add(unserializableMessage as Message);
                }
            }
            List<ArraySegment<byte>> serialized;
            int headerLengthOut;
            ret = OutgoingMessageSender.SerializeMessages(mlist, out serialized, out headerLengthOut, (Message m, Exception exc) => { });

            // Message.Serialize(bool) is guranteed to correct by other tests, so we just check the MetaHeader and Lengths
            Assert.AreEqual(true, ret);
            // The number of messages is 4, because 1 message is invalid and not serialized
            Assert.IsTrue(AreByteArraysAreEqual(serialized[0].Array, new byte[4] { 4, 0, 0, 0 }));
            //Assert.IsTrue(AreByteArraysAreEqual(serialized[1].Array, new byte[4] { (byte)mlist[0].headerLength, 0, 0, 0 }));
            //Assert.IsTrue(AreByteArraysAreEqual(serialized[2].Array, new byte[4] { (byte)mlist[0].bodyLength, 0, 0, 0 }));
            //Assert.IsTrue(AreByteArraysAreEqual(serialized[3].Array, new byte[4] { (byte)mlist[1].headerLength, 0, 0, 0 }));
            //Assert.IsTrue(AreByteArraysAreEqual(serialized[4].Array, new byte[4] { (byte)mlist[1].bodyLength, 0, 0, 0 }));
            //Assert.IsTrue(AreByteArraysAreEqual(serialized[5].Array, new byte[4] { (byte)mlist[2].headerLength, 0, 0, 0 }));
            //Assert.IsTrue(AreByteArraysAreEqual(serialized[6].Array, new byte[4] { (byte)mlist[2].bodyLength, 0, 0, 0 }));
            //// skip mlist[3] because it is invalid and not serialized
            //Assert.IsTrue(AreByteArraysAreEqual(serialized[7].Array, new byte[4] { (byte)mlist[4].headerLength, 0, 0, 0 }));
            //Assert.IsTrue(AreByteArraysAreEqual(serialized[8].Array, new byte[4] { (byte)mlist[4].bodyLength, 0, 0, 0 }));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_ListOfMessages_AllError()
        {
            MessagingStatisticsGroup.Init(false);
            InitializeForTesting();
            List<Message> mlist = new List<Message>();
            bool ret;

            // All messages are invalid
            for (int i = 0; i < 5; i++)
            {
                 Object unserializableMessage = new Object();
                 mlist.Add(unserializableMessage as Message);
            }
            List<ArraySegment<byte>> serialized;
            int headerLengthOut;
            ret = OutgoingMessageSender.SerializeMessages(mlist, out serialized, out headerLengthOut, (Message m, Exception exc) => { });

            // SerializeMessages returns false, which means no message could be serialized
            Assert.AreEqual(false, ret);
            Assert.AreEqual(null, serialized);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void IsOrleansShallowCopyable()
        {
            Type t = typeof(Dictionary<string, object>);
            Assert.IsFalse(t.IsOrleansShallowCopyable(), "IsOrleansShallowCopyable: {0}", t.Name);
            t = typeof(Dictionary<string, int>);
            Assert.IsFalse(t.IsOrleansShallowCopyable(), "IsOrleansShallowCopyable: {0}", t.Name);
        }

        private object OrleansSerializationLoop(object input, bool includeWire = true)
        {
            var copy = SerializationManager.DeepCopy(input);
            if (includeWire)
            {
                copy = SerializationManager.RoundTripSerializationForTesting(copy);
            }
            return copy;
        }

        private object DotNetSerializationLoop(object input)
        {
            byte[] bytes;
            object deserialized;
            using (var str = new MemoryStream())
            {
                IFormatter formatter = new BinaryFormatter();
                formatter.Serialize(str, input);
                str.Flush();
                bytes = str.ToArray();
            }
            using (var inStream = new MemoryStream(bytes))
            {
                IFormatter formatter = new BinaryFormatter();
                deserialized = formatter.Deserialize(inStream);
            }
            return deserialized;
        }

        private void ValidateDictionary<K, V>(Dictionary<K, V> source, object deserialized, string type)
        {
            Assert.IsInstanceOfType(deserialized, typeof(Dictionary<K, V>), "Type is wrong after round-trip of " + type + " dict");
            Dictionary<K, V> result = deserialized as Dictionary<K, V>;
            Assert.AreEqual(source.Count, result.Count, "Count is wrong after round-trip of " + type + " dict");
            foreach (var pair in source)
            {
                Assert.IsTrue(result.ContainsKey(pair.Key), "Key " + pair.Key.ToString() + " is missing after round-trip of " + type + " dict");
                Assert.AreEqual<V>(pair.Value, result[pair.Key], "Key " + pair.Key.ToString() + " has wrong value after round-trip of " + type + " dict");
            }
        }

        private void ValidateSortedDictionary<K, V>(SortedDictionary<K, V> source, object deserialized, string type)
        {
            Assert.IsInstanceOfType(deserialized, typeof(SortedDictionary<K, V>), "Type is wrong after round-trip of " + type + " sorted dict");
            SortedDictionary<K, V> result = deserialized as SortedDictionary<K, V>;
            Assert.AreEqual(source.Count, result.Count, "Count is wrong after round-trip of " + type + " sorted dict");
            foreach (var pair in source)
            {
                Assert.IsTrue(result.ContainsKey(pair.Key), "Key " + pair.Key.ToString() + " is missing after round-trip of " + type + " sorted dict");
                Assert.AreEqual<V>(pair.Value, result[pair.Key], "Key " + pair.Key.ToString() + " has wrong value after round-trip of " + type + " sorted dict");
            }

            var sourceKeys = source.Keys.GetEnumerator();
            var resultKeys = result.Keys.GetEnumerator();
            while (sourceKeys.MoveNext() && resultKeys.MoveNext())
            {
                Assert.AreEqual<K>(sourceKeys.Current, resultKeys.Current, "Keys out of order after round-trip of " + type + " sorted dict");
            }
        }

        private void ValidateSortedList<K, V>(SortedList<K, V> source, object deserialized, string type)
        {
            Assert.IsInstanceOfType(deserialized, typeof(SortedList<K, V>), "Type is wrong after round-trip of " + type + " sorted list");
            SortedList<K, V> result = deserialized as SortedList<K, V>;
            Assert.AreEqual(source.Count, result.Count, "Count is wrong after round-trip of " + type + " sorted list");
            foreach (var pair in source)
            {
                Assert.IsTrue(result.ContainsKey(pair.Key), "Key " + pair.Key.ToString() + " is missing after round-trip of " + type + " sorted list");
                Assert.AreEqual<V>(pair.Value, result[pair.Key], "Key " + pair.Key.ToString() + " has wrong value after round-trip of " + type + " sorted list");
            }

            var sourceKeys = source.Keys.GetEnumerator();
            var resultKeys = result.Keys.GetEnumerator();
            while (sourceKeys.MoveNext() && resultKeys.MoveNext())
            {
                Assert.AreEqual<K>(sourceKeys.Current, resultKeys.Current, "Keys out of order after round-trip of " + type + " sorted list");
            }
        }

        private void ValidateList<T>(List<T> expected, List<T> result, string type)
        {
            Assert.AreEqual(expected.Count, result.Count, "Count is wrong after round-trip of " + type + " list");
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.AreEqual<T>(expected[i], result[i], "Item " + i + " is wrong after round trip of " + type + " list");
            }
        }

        private void ValidateArray<T>(T[] expected, object deserialized, string type)
        {
            Assert.IsInstanceOfType(deserialized, typeof(T[]), "Type is wrong after round-trip of " + type + " array");
            var result = deserialized as T[];
            Assert.AreEqual(expected.Length, result.Length, "Length is wrong after round-trip of " + type + " array");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual<T>(expected[i], result[i], "Item " + i + " is wrong after round trip of " + type + " array");
            }
        }

        private void ValidateArrayOfArrays<T>(T[][] expected, object deserialized, string type)
        {
            Assert.IsInstanceOfType(deserialized, typeof(T[][]), "Type is wrong after round-trip of " + type + " array of arrays");
            var result = deserialized as T[][];
            Assert.AreEqual(expected.Length, result.Length, "Length is wrong after round-trip of " + type + " array of arrays");
            for (int i = 0; i < expected.Length; i++)
            {
                ValidateArray<T>(expected[i], result[i], "Array of " + type + "[" + i + "] ");
            }
        }

        private void ValidateArrayOfArrayOfArrays<T>(T[][][] expected, object deserialized, string type)
        {
            Assert.IsInstanceOfType(deserialized, typeof(T[][][]), "Type is wrong after round-trip of " + type + " array of arrays");
            var result = deserialized as T[][][];
            Assert.AreEqual(expected.Length, result.Length, "Length is wrong after round-trip of " + type + " array of arrays");
            for (int i = 0; i < expected.Length; i++)
            {
                ValidateArrayOfArrays<T>(expected[i], result[i], "Array of " + type + "[" + i + "][]");
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_CircularReference()
        {
            var c1 = new CircularTest1();
            var c2 = new CircularTest2();
            c2.CircularTest1List.Add(c1);
            c1.CircularTest2 = c2;

            var deserialized = (CircularTest1)OrleansSerializationLoop(c1);
            Assert.AreEqual(c1.CircularTest2.CircularTest1List.Count, deserialized.CircularTest2.CircularTest1List.Count);
            Assert.AreEqual(deserialized, deserialized.CircularTest2.CircularTest1List[0]);

            deserialized = (CircularTest1)OrleansSerializationLoop(c1, true);
            Assert.AreEqual(c1.CircularTest2.CircularTest1List.Count, deserialized.CircularTest2.CircularTest1List.Count);
            Assert.AreEqual(deserialized, deserialized.CircularTest2.CircularTest1List[0]);
        }

        [Serializable]
        public class CircularTest1
        {
            public CircularTest2 CircularTest2 { get; set; }
        }
        [Serializable]
        public class CircularTest2
        {
            public CircularTest2()
            {
                CircularTest1List = new List<CircularTest1>();                   
            }
            public List<CircularTest1> CircularTest1List { get; set; }
        }
    }
}

// ReSharper restore NotAccessedVariable