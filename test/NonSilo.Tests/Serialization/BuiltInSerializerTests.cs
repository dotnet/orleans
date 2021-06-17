using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.TypeSystem;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using Orleans.Utilities;
using TestExtensions;
using TestGrainInterfaces;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable NotAccessedVariable

namespace UnitTests.Serialization
{
    /// <summary>
    /// Test the built-in serializers
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection), TestCategory("Serialization")]
    public class BuiltInSerializerTests
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture environment;

        public BuiltInSerializerTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.environment = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("CodeGen")]
        public void InternalSerializableTypesHaveSerializers()
        {
            Assert.True(
                environment.Serializer.CanSerialize<int>(),
                $"Should be able to serialize internal type {nameof(Int32)}.");
            Assert.True(
                environment.Serializer.CanSerialize(typeof(List<int>)),
                $"Should be able to serialize internal type {nameof(List<int>)}.");
            Assert.True(
                environment.Serializer.CanSerialize(typeof(PubSubGrainState)),
                $"Should be able to serialize internal type {nameof(PubSubGrainState)}.");
            Assert.True(
                environment.Serializer.CanSerialize(typeof(EventHubBatchContainer)),
                $"Should be able to serialize internal type {nameof(EventHubBatchContainer)}.");
            Assert.True(
                environment.Serializer.CanSerialize(typeof(EventHubSequenceTokenV2)),
                $"Should be able to serialize internal type {nameof(EventHubSequenceTokenV2)}.");
        }

        [Fact(Skip = "See https://github.com/dotnet/orleans/issues/3531"), TestCategory("BVT"), TestCategory("CodeGen")]
        public void ValueTupleTypesHasSerializer()
        {
            Assert.True(
                environment.Serializer.CanSerialize(typeof(ValueTuple<int, AddressAndTag>)),
                $"Should be able to serialize internal type {nameof(ValueTuple<int, AddressAndTag>)}.");
        }

        /// <summary>
        /// Tests that the default (non-fallback) serializer can handle complex classes.
        /// </summary>
        /// <param name="serializerToUse"></param>
        [Fact, TestCategory("BVT")]
        public void Serialize_ComplexAccessibleClass()
        {
            var expected = new AnotherConcreteClass
            {
                Int = 89,
                AnotherString = Guid.NewGuid().ToString(),
                NonSerializedInt = 39,
                Enum = SomeAbstractClass.SomeEnum.Something,
            };

            expected.Classes = new SomeAbstractClass[]
            {
                expected,
                new AnotherConcreteClass
                {
                    AnotherString = "hi",
                    Interfaces = new List<ISomeInterface> { expected }
                }
            };
            expected.Interfaces = new List<ISomeInterface>
            {
                expected.Classes[1]
            };
            expected.SetObsoleteInt(38);

            var actual = (AnotherConcreteClass)OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, expected);

            Assert.Equal(expected.Int, actual.Int);
            Assert.Equal(expected.Enum, actual.Enum);
            Assert.Equal(expected.AnotherString, actual.AnotherString);
            Assert.Equal(expected.Classes.Length, actual.Classes.Length);
            Assert.Equal(expected.Classes[1].Interfaces[0].Int, actual.Classes[1].Interfaces[0].Int);
            Assert.Equal(expected.Interfaces[0].Int, actual.Interfaces[0].Int);
            Assert.Equal(actual.Interfaces[0], actual.Classes[1]);
            Assert.NotEqual(expected.NonSerializedInt, actual.NonSerializedInt);
            Assert.Equal(0, actual.NonSerializedInt);
            Assert.Equal(expected.GetObsoleteInt(), actual.GetObsoleteInt());
            Assert.Null(actual.SomeGrainReference);
        }

        [Fact, TestCategory("BVT")]
        public void Serialize_Type()
        {

            // Test serialization of Type.
            var expected = typeof(int);
            var actual = (Type)OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, expected);
            Assert.Equal(expected.AssemblyQualifiedName, actual.AssemblyQualifiedName);

            // Test serialization of RuntimeType.
            expected = 8.GetType();
            actual = (Type)OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, expected);
            Assert.Equal(expected.AssemblyQualifiedName, actual.AssemblyQualifiedName);
        }

        [Fact, TestCategory("BVT")]
        public void Serialize_ComplexStruct()
        {

            // Test struct serialization.
            var expected = new SomeStruct(10) { Id = Guid.NewGuid(), PublicValue = 6, ValueWithPrivateGetter = 7 };
            expected.SetValueWithPrivateSetter(8);
            expected.SetPrivateValue(9);
            var actual = (SomeStruct)OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, expected);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.ReadonlyField, actual.ReadonlyField);
            Assert.Equal(expected.PublicValue, actual.PublicValue);
            Assert.Equal(expected.ValueWithPrivateSetter, actual.ValueWithPrivateSetter);
            Assert.Null(actual.SomeGrainReference);
            Assert.Equal(expected.GetPrivateValue(), actual.GetPrivateValue());
            Assert.Equal(expected.GetValueWithPrivateGetter(), actual.GetValueWithPrivateGetter());
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_ActivationAddress()
        {
            GrainId grain = LegacyGrainId.NewId();
            var addr = ActivationAddress.GetAddress(null, grain, default);
            object deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, addr, false);
            Assert.IsAssignableFrom<ActivationAddress>(deserialized);
            Assert.Null(((ActivationAddress)deserialized).Activation); //Activation no longer null after copy
            Assert.Null(((ActivationAddress)deserialized).Silo); //Silo no longer null after copy
            Assert.Equal(grain, ((ActivationAddress)deserialized).Grain); //Grain different after copy
            deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, addr);
            Assert.IsAssignableFrom<ActivationAddress>(deserialized); //ActivationAddress full serialization loop as wrong type
            Assert.Null(((ActivationAddress)deserialized).Activation); //Activation no longer null after full serialization loop
            Assert.Null(((ActivationAddress)deserialized).Silo); //Silo no longer null after full serialization loop
            Assert.Equal(grain, ((ActivationAddress)deserialized).Grain); //Grain different after copy
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_EmptyList()
        {
            var list = new List<int>();
            var deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, list, false);
            Assert.IsAssignableFrom<List<int>>(deserialized);  //Empty list of integers copied as wrong type"
            ValidateList(list, (List<int>)deserialized, "int (empty, copy)");
            deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, list);
            Assert.IsAssignableFrom<List<int>>(deserialized); //Empty list of integers full serialization loop as wrong type
            ValidateList(list, (List<int>)deserialized, "int (empty)");
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_BasicDictionaries()
        {

            Dictionary<string, string> source1 = new Dictionary<string, string>();
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            var deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source1);
            ValidateDictionary<string, string>(source1, deserialized, "string/string");

            Dictionary<int, DateTime> source2 = new Dictionary<int, DateTime>();
            source2[3] = DateTime.Now;
            source2[27] = DateTime.Now.AddHours(2);
            deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source2);
            ValidateDictionary<int, DateTime>(source2, deserialized, "int/date");
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_ReadOnlyDictionary()
        {
            Dictionary<string, string> source1 = new Dictionary<string, string>();
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            var readOnlySource1 = new ReadOnlyDictionary<string, string>(source1);
            var deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, readOnlySource1);
            ValidateReadOnlyDictionary(readOnlySource1, deserialized, "string/string");

            Dictionary<int, DateTime> source2 = new Dictionary<int, DateTime>();
            source2[3] = DateTime.Now;
            source2[27] = DateTime.Now.AddHours(2);
            var readOnlySource2 = new ReadOnlyDictionary<int, DateTime>(source2);
            deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, readOnlySource2);
            ValidateReadOnlyDictionary(readOnlySource2, deserialized, "int/date");
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_DictionaryWithComparer()
        {
            Dictionary<string, string> source1 = new Dictionary<string, string>(new CaseInsensitiveStringEquality());
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            var deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source1);
            ValidateDictionary<string, string>(source1, deserialized, "case-insensitive string/string");
            Dictionary<string, string> result1 = deserialized as Dictionary<string, string>;
            Assert.Equal(source1["Hello"], result1["hElLo"]); //Round trip for case insensitive string/string dictionary lost the custom comparer

            Dictionary<int, DateTime> source2 = new Dictionary<int, DateTime>(new Mod5IntegerComparer());
            source2[3] = DateTime.Now;
            source2[27] = DateTime.Now.AddHours(2);
            deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source2);
            ValidateDictionary<int, DateTime>(source2, deserialized, "int/date");
            Dictionary<int, DateTime> result2 = (Dictionary<int, DateTime>)deserialized;
            Assert.Equal<DateTime>(source2[3], result2[13]);  //Round trip for case insensitive int/DateTime dictionary lost the custom comparer"
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_SortedDictionaryWithComparer()
        {
            var source1 = new SortedDictionary<string, string>(new CaseInsensitiveStringComparer());
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            object deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source1);
            ValidateSortedDictionary<string, string>(source1, deserialized, "string/string");
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_SortedListWithComparer()
        {
            var source1 = new SortedList<string, string>(new CaseInsensitiveStringComparer());
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            object deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source1);
            ValidateSortedList<string, string>(source1, deserialized, "string/string");
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_HashSetWithComparer()
        {
            var source1 = new HashSet<string>(new CaseInsensitiveStringEquality());
            source1.Add("one");
            source1.Add("two");
            source1.Add("three");
            var deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source1);
            Assert.IsAssignableFrom(source1.GetType(), deserialized); //Type is wrong after round-trip of string hash set with comparer
            var result = deserialized as HashSet<string>;
            Assert.Equal(source1.Count, result.Count); //Count is wrong after round-trip of string hash set with comparer
#pragma warning disable xUnit2017 // Do not use Contains() to check if a value exists in a collection
            foreach (var key in source1)
            {
                Assert.True(result.Contains(key)); //key is missing after round-trip of string hash set with comparer
            }
            Assert.True(result.Contains("One")); //Comparer is wrong after round-trip of string hash set with comparer
#pragma warning restore xUnit2017 // Do not use Contains() to check if a value exists in a collection
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_Stack()
        {
            var source1 = new Stack<string>();
            source1.Push("one");
            source1.Push("two");
            source1.Push("three");
            object deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source1);
            Assert.IsAssignableFrom(source1.GetType(), deserialized); //Type is wrong after round-trip of string stack
            var result = deserialized as Stack<string>;
            Assert.Equal(source1.Count, result.Count); //Count is wrong after round-trip of string stack

            var srcIter = source1.GetEnumerator();
            var resIter = result.GetEnumerator();
            while (srcIter.MoveNext() && resIter.MoveNext())
            {
                Assert.Equal(srcIter.Current, resIter.Current); //Data is wrong after round-trip of string stack
            }
        }

        /// <summary>
        /// Tests that the <see cref="IOnDeserialized"/> callback is invoked after deserialization.
        /// </summary>
        /// <param name="serializerToUse"></param>
        [Fact, TestCategory("Functional")]
        public void Serialize_TypeWithOnDeserializedHook()
        {
            var input = new TypeWithOnDeserializedHook
            {
                Int = 5
            };
            var deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, input);
            var result = Assert.IsType<TypeWithOnDeserializedHook>(deserialized);
            Assert.Equal(input.Int, result.Int);
            Assert.Null(input.Context);
            Assert.NotNull(result.Context);
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_SortedSetWithComparer()
        {
            var source1 = new SortedSet<string>(new CaseInsensitiveStringComparer());
            source1.Add("one");
            source1.Add("two");
            source1.Add("three");
            object deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source1);
            Assert.IsAssignableFrom(source1.GetType(), deserialized); //Type is wrong after round-trip of string sorted set with comparer
            var result = (SortedSet<string>)deserialized;
            Assert.Equal(source1.Count, result.Count); //Count is wrong after round-trip of string sorted set with comparer
#pragma warning disable xUnit2017 // Do not use Contains() to check if a value exists in a collection
            foreach (var key in source1)
            {
                Assert.True(result.Contains(key)); //key is missing after round-trip of string sorted set with comparer
            }
            Assert.True(result.Contains("One")); //Comparer is wrong after round-trip of string sorted set with comparer
#pragma warning restore xUnit2017 // Do not use Contains() to check if a value exists in a collection
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_Array()
        {
            var source1 = new int[] { 1, 3, 5 };
            object deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source1);
            ValidateArray<int>(source1, deserialized, "int");

            var source2 = new string[] { "hello", "goodbye", "yes", "no", "", "I don't know" };
            deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source2);
            ValidateArray<string>(source2, deserialized, "string");

            var source3 = new sbyte[] { 1, 3, 5 };
            deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source3);
            ValidateArray<sbyte>(source3, deserialized, "sbyte");

            var source4 = new byte[] { 1, 3, 5 };
            deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source4);
            ValidateArray<byte>(source4, deserialized, "byte");
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_ArrayOfArrays()
        {
            var source1 = new[] { new[] { 1, 3, 5 }, new[] { 10, 20, 30 }, new[] { 17, 13, 11, 7, 5, 3, 2 } };
            object deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source1);
            ValidateArrayOfArrays(source1, deserialized, "int");

            var source2 = new[] { new[] { "hello", "goodbye", "yes", "no", "", "I don't know" }, new[] { "yes" } };
            deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source2);
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
            deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source3);
            var result = Assert.IsAssignableFrom<HashSet<string>[][]>(deserialized); //Array of arrays of hash sets type is wrong on deserialization
            Assert.Equal(3, result.Length); //Outer array size wrong on array of array of sets
            Assert.Equal(2, result[0][0].Count); //Inner set size wrong on array of array of sets, element 0,0
            Assert.Empty(result[0][1]); //Inner set size wrong on array of array of sets, element 0,1
            Assert.Single(result[1][0]); //Inner set size wrong on array of array of sets, element 1,0
            Assert.Null(result[1][1]); //Inner set not null on array of array of sets, element 1, 1
            Assert.Single(result[1][2]); //Inner set size wrong on array of array of sets, element 1,2
            Assert.Single(result[2][0]); //Inner set size wrong on array of array of sets, element 2,0

            var source4 = new GrainReference[3][];
            source4[0] = new GrainReference[2];
            source4[1] = new GrainReference[3];
            source4[2] = new GrainReference[1];
            source4[0][0] = (GrainReference)environment.InternalGrainFactory.GetGrain(LegacyGrainId.NewId());
            source4[0][1] = (GrainReference)environment.InternalGrainFactory.GetGrain(LegacyGrainId.NewId());
            source4[1][0] = (GrainReference)environment.InternalGrainFactory.GetGrain(LegacyGrainId.NewId());
            source4[1][1] = (GrainReference)environment.InternalGrainFactory.GetGrain(LegacyGrainId.NewId());
            source4[1][2] = (GrainReference)environment.InternalGrainFactory.GetGrain(LegacyGrainId.NewId());
            source4[2][0] = (GrainReference)environment.InternalGrainFactory.GetGrain(LegacyGrainId.NewId());
            deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source4);
            ValidateArrayOfArrays(source4, deserialized, "grain reference");

            var source5 = new GrainReference[32][];
            for (int i = 0; i < source5.Length; i++)
            {
                source5[i] = new GrainReference[64];
                for (int j = 0; j < source5[i].Length; j++)
                {
                    source5[i][j] = (GrainReference)environment.InternalGrainFactory.GetGrain(LegacyGrainId.NewId());
                }
            }
            deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source5);
            ValidateArrayOfArrays(source5, deserialized, "grain reference (large)");
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_ArrayOfArrayOfArrays()
        {
            var source1 = new[] { new[] { 1, 3, 5 }, new[] { 10, 20, 30 }, new[] { 17, 13, 11, 7, 5, 3, 2 } };
            var source2 = new[] { new[] { 1, 3 }, new[] { 10, 20 }, new[] { 17, 13, 11, 7, 5 } };
            var source3 = new[] { new[] { 1, 3, 5 }, new[] { 10, 20, 30 } };
            var source = new[] { source1, source2, source3 };
            object deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, source);
            ValidateArrayOfArrayOfArrays(source, deserialized, "int");
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_ReadOnlyCollection()
        {
            var source1 = new List<string> { "Yes", "No" };
            var collection = new ReadOnlyCollection<string>(source1);
            var deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, collection);
            ValidateReadOnlyCollectionList(collection, deserialized, "string/string");
        }

        private class BanningTypeResolver : TypeResolver
        {
            private readonly CachedTypeResolver _resolver = new CachedTypeResolver();
            private readonly HashSet<Type> _blockedTypes;

            public BanningTypeResolver(params Type[] blockedTypes)
            {
                _blockedTypes = new HashSet<Type>();
                foreach (var type in blockedTypes ?? Array.Empty<Type>())
                {
                    _blockedTypes.Add(type);
                }
            }

            public override Type ResolveType(string name)
            {
                var result = _resolver.ResolveType(name);
                if (_blockedTypes.Contains(result))
                {
                    result = null;
                }

                return result;
            }

            public override bool TryResolveType(string name, out Type type)
            {
                if (_resolver.TryResolveType(name, out type))
                {
                    if (_blockedTypes.Contains(type))
                    {
                        type = null;
                        return false;
                    }

                    return true;
                }

                return false;
            }
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_ObjectIdentity()
        {
            var val = new List<string> { "first", "second" };

            var val2 = new List<string> { "first", "second" };

            var source = new Dictionary<string, List<string>>();
            source["one"] = val;
            source["two"] = val;
            source["three"] = val2;
            Assert.Same(source["one"], source["two"]); //Object identity lost before round trip of string/list dict!!!

            var deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier,  source);
            var result = Assert.IsAssignableFrom<Dictionary<string, List<string>>>(deserialized); //Type is wrong after round-trip of string/list dict
            Assert.Equal(source.Count, result.Count); //Count is wrong after round-trip of string/list dict

            List<string> list1;
            List<string> list2;
            List<string> list3;
            Assert.True(result.TryGetValue("one", out list1)); //Key 'one' not found after round trip of string/list dict
            Assert.True(result.TryGetValue("two", out list2)); //Key 'two' not found after round trip of string/list dict
            Assert.True(result.TryGetValue("three", out list3)); //Key 'three' not found after round trip of string/list dict

            ValidateList<string>(val, list1, "string");
            ValidateList<string>(val, list2, "string");
            ValidateList<string>(val2, list3, "string");

            Assert.Same(list1, list2); //Object identity lost after round trip of string/list dict
            Assert.NotSame(list2, list3); //Object identity gained after round trip of string/list dict
            Assert.NotSame(list1, list3); //Object identity gained after round trip of string/list dict
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_Unrecognized()
        {
            var test1 = new Unrecognized { A = 3, B = 27 };
            var raw = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, test1, false);
            var result = Assert.IsAssignableFrom<Unrecognized>(raw); //Type is wrong after deep copy of unrecognized
            Assert.Equal(3, result.A);  //Property A is wrong after deep copy of unrecognized"
            Assert.Equal(27, result.B);  //Property B is wrong after deep copy of unrecognized"

            var test2 = new Unrecognized[3];
            for (int i = 0; i < 3; i++)
            {
                test2[i] = new Unrecognized { A = i, B = 2 * i };
            }
            raw = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier, test2);
            Assert.IsAssignableFrom<Unrecognized[]>(raw); //Type is wrong after round trip of array of unrecognized
            var result2 = (Unrecognized[])raw;
            Assert.Equal(3, result2.Length); //Array length is wrong after round trip of array of unrecognized
            for (int j = 0; j < 3; j++)
            {
                Assert.Equal(j, result2[j].A); //Property A at index " + j + "is wrong after round trip of array of unrecognized
                Assert.Equal(2 * j, result2[j].B); //Property B at index " + j + "is wrong after round trip of array of unrecognized
            }
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_Immutable()
        {
            var test1 = new ImmutableType(3, 27);
            var raw = environment.DeepCopier.Copy<object>(test1);
            Assert.IsAssignableFrom<ImmutableType>(raw); //Type is wrong after deep copy of [Immutable] type
            Assert.Same(test1, raw); //Deep copy of [Immutable] object made a copy instead of just copying the pointer

            var test2list = new List<int>();
            for (int i = 0; i < 3; i++)
            {
                test2list.Add(i);
            }
            var test2 = new Immutable<List<int>>(test2list);
            raw = environment.DeepCopier.Copy<object>(test2);
            Assert.IsAssignableFrom<Immutable<List<int>>>(raw); //Type is wrong after round trip of array of Immutable<>
            Assert.Same(test2.Value, ((Immutable<List<int>>)raw).Value); //Deep copy of Immutable<> object made a copy instead of just copying the pointer

            var test3 = new EmbeddedImmutable("test", 1, 2, 3, 4);
            raw = environment.DeepCopier.Copy<object>(test3);
            Assert.IsAssignableFrom<EmbeddedImmutable>(raw); //Type is wrong after deep copy of type containing an Immutable<> field
            Assert.Same(test3.B.Value, ((EmbeddedImmutable)raw).B.Value); //Deep copy of embedded [Immutable] object made a copy instead of just copying the pointer
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_GrainReference()
        {
            GrainId grainId = LegacyGrainId.NewId();
            GrainReference input = (GrainReference)environment.InternalGrainFactory.GetGrain(grainId);

            object deserialized = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier,  input);

            var grainRef = Assert.IsAssignableFrom<GrainReference>(deserialized); //GrainReference copied as wrong type
            Assert.Equal(grainId, grainRef.GrainId); //GrainId different after copy
            Assert.Equal(input, grainRef); //Wrong contents after round-trip of input
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

        internal static object OrleansSerializationLoop(Serializer serializer, DeepCopier copier, object input, bool includeWire = true)
        {
            var copy = copier.Copy(input);
            if (includeWire)
            {
                copy = serializer.RoundTripSerializationForTesting(copy);
            }
            return copy;
        }

        private void ValidateDictionary<K, V>(Dictionary<K, V> source, object deserialized, string type)
        {
            var result = Assert.IsAssignableFrom<Dictionary<K, V>>(deserialized); //Type is wrong after round-trip of dict
            ValidateDictionaryContent(source, result, type);
        }

        private void ValidateDictionaryContent<K, V>(IDictionary<K, V> source, IDictionary<K, V> result, string type)
        {
            Assert.Equal(source.Count, result.Count);  //Count is wrong after round-trip of " + type + " dict"
            foreach (var pair in source)
            {
                Assert.True(result.ContainsKey(pair.Key), "Key " + pair.Key.ToString() + " is missing after round-trip of " + type + " dict");
                Assert.Equal<V>(pair.Value, result[pair.Key]); //Key has wrong value after round-trip
            }
        }

        private void ValidateReadOnlyDictionary<K, V>(ReadOnlyDictionary<K, V> source, object deserialized, string type)
        {
            var result = Assert.IsAssignableFrom<ReadOnlyDictionary<K, V>>(deserialized); //Type is wrong after round-trip
            ValidateDictionaryContent(source, result, type);
        }

        private void ValidateSortedDictionary<K, V>(SortedDictionary<K, V> source, object deserialized, string type)
        {
            Assert.IsAssignableFrom<SortedDictionary<K, V>>(deserialized);
            SortedDictionary<K, V> result = deserialized as SortedDictionary<K, V>;
            Assert.Equal(source.Count, result.Count); //Count is wrong after round-trip of " + type + " sorted dict
            foreach (var pair in source)
            {
                Assert.True(result.ContainsKey(pair.Key)); //Key " + pair.Key.ToString() + " is missing after round-trip of " + type + " sorted dict
                Assert.Equal<V>(pair.Value, result[pair.Key]); //Key " + pair.Key.ToString() + " has wrong value after round-trip of " + type + " sorted dict
            }

            var sourceKeys = source.Keys.GetEnumerator();
            var resultKeys = result.Keys.GetEnumerator();
            while (sourceKeys.MoveNext() && resultKeys.MoveNext())
            {
                Assert.Equal<K>(sourceKeys.Current, resultKeys.Current); //Keys out of order after round-trip of " + type + " sorted dict
            }
        }

        private void ValidateSortedList<K, V>(SortedList<K, V> source, object deserialized, string type)
        {
            Assert.IsAssignableFrom<SortedList<K, V>>(deserialized);
            SortedList<K, V> result = deserialized as SortedList<K, V>;
            Assert.Equal(source.Count, result.Count);  //Count is wrong after round-trip of " + type + " sorted list"
            foreach (var pair in source)
            {
                Assert.True(result.ContainsKey(pair.Key)); //Key " + pair.Key.ToString() + " is missing after round-trip of " + type + " sorted list
                Assert.Equal<V>(pair.Value, result[pair.Key]); //Key " + pair.Key.ToString() + " has wrong value after round-trip of " + type + " sorted list
            }

            var sourceKeys = source.Keys.GetEnumerator();
            var resultKeys = result.Keys.GetEnumerator();
            while (sourceKeys.MoveNext() && resultKeys.MoveNext())
            {
                Assert.Equal<K>(sourceKeys.Current, resultKeys.Current); //Keys out of order after round-trip of " + type + " sorted list
            }
        }

        private void ValidateReadOnlyCollectionList<T>(ReadOnlyCollection<T> expected, object deserialized, string type)
        {
            Assert.IsAssignableFrom<ReadOnlyCollection<T>>(deserialized); //Type is wrong after round-trip of " + type + " array
            ValidateList(expected, deserialized as IList<T>, type);
        }

        private void ValidateList<T>(IList<T> expected, IList<T> result, string type)
        {
            Assert.Equal(expected.Count, result.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal<T>(expected[i], result[i]); //Item " + i + " is wrong after round trip of " + type + " list
            }
        }

        private void ValidateArray<T>(T[] expected, object deserialized, string type)
        {
            var result = Assert.IsAssignableFrom<T[]>(deserialized);
            Assert.Equal(expected.Length, result.Length);  //Length is wrong after round-trip of " + type + " array"
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal<T>(expected[i], result[i]);  //Item " + i + " is wrong after round trip of " + type + " array"
            }
        }

        private void ValidateArrayOfArrays<T>(T[][] expected, object deserialized, string type)
        {
            var result = Assert.IsAssignableFrom<T[][]>(deserialized);  //Type is wrong after round-trip of " + type + " array of arrays"
            Assert.Equal(expected.Length, result.Length);  //Length is wrong after round-trip of " + type + " array of arrays"
            for (int i = 0; i < expected.Length; i++)
            {
                ValidateArray<T>(expected[i], result[i], "Array of " + type + "[" + i + "] ");
            }
        }

        private void ValidateArrayOfArrayOfArrays<T>(T[][][] expected, object deserialized, string type)
        {
            var result = Assert.IsAssignableFrom<T[][][]>(deserialized);  //Type is wrong after round-trip of " + type + " array of arrays"
            Assert.Equal(expected.Length, result.Length);  //Length is wrong after round-trip of " + type + " array of arrays"
            for (int i = 0; i < expected.Length; i++)
            {
                ValidateArrayOfArrays<T>(expected[i], result[i], "Array of " + type + "[" + i + "][]");
            }
        }

        [Fact, TestCategory("Functional")]
        public void Serialize_CircularReference()
        {
            var c1 = new CircularTest1();
            var c2 = new CircularTest2();
            c2.CircularTest1List.Add(c1);
            c1.CircularTest2 = c2;

            var deserialized = (CircularTest1)OrleansSerializationLoop(environment.Serializer, environment.DeepCopier,  c1);
            Assert.Equal(c1.CircularTest2.CircularTest1List.Count, deserialized.CircularTest2.CircularTest1List.Count);
            Assert.Same(deserialized, deserialized.CircularTest2.CircularTest1List[0]);

            deserialized = (CircularTest1)OrleansSerializationLoop(environment.Serializer, environment.DeepCopier,  c1, true);
            Assert.Equal(c1.CircularTest2.CircularTest1List.Count, deserialized.CircularTest2.CircularTest1List.Count);
            Assert.Same(deserialized, deserialized.CircularTest2.CircularTest1List[0]);
        }
        
        [Fact, TestCategory("Functional")]
        public void Serialize_Enums()
        {
            var result = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier,  IntEnum.Value2);
            var typedResult = Assert.IsType<IntEnum>(result);
            Assert.Equal(IntEnum.Value2, typedResult);

            var result2 = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier,  UShortEnum.Value3);
            var typedResult2 = Assert.IsType<UShortEnum>(result2);
            Assert.Equal(UShortEnum.Value3, typedResult2);

            var test = new ClassWithEnumTestData { EnumValue = TestEnum.Third, Enemy = CampaignEnemyTestType.Enemy3 };
            var result3 = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier,  test);
            var typedResult3 = Assert.IsType<ClassWithEnumTestData>(result3);

            Assert.Equal(TestEnum.Third, typedResult3.EnumValue);
            Assert.Equal(CampaignEnemyTestType.Enemy3, typedResult3.Enemy);

            var result4 = OrleansSerializationLoop(environment.Serializer, environment.DeepCopier,  CampaignEnemyType.Enemy3);
            var typedResult4 = Assert.IsType<CampaignEnemyType>(result4);
            Assert.Equal(CampaignEnemyType.Enemy3, typedResult4);
        }
    }
}

// ReSharper restore NotAccessedVariable