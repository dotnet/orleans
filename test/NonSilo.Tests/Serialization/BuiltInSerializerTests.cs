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
using Orleans.Runtime.GrainDirectory;
using Orleans.Serialization;
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
    public class BuiltInSerializerCollectionFixture
    {
        public ConcurrentDictionary<BuiltInSerializerTests.SerializerToUse, SerializationTestEnvironment> Environments { get; } =
            new ConcurrentDictionary<BuiltInSerializerTests.SerializerToUse, SerializationTestEnvironment>();
    }

    /// <summary>
    /// Test the built-in serializers
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection), TestCategory("Serialization")]
    public class BuiltInSerializerTests
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture defaultFixture;
        private readonly BuiltInSerializerCollectionFixture serializerFixture;

        public enum SerializerToUse
        {
            Default,
            IlBasedFallbackSerializer,
            NoFallback
        }

        private SerializationTestEnvironment InitializeSerializer(SerializerToUse serializerToUse)
        {
            if (serializerToUse == SerializerToUse.Default)
            {
                return this.defaultFixture;
            }

            return this.serializerFixture.Environments.GetOrAdd(
                serializerToUse,
                _ =>
                {
                    Type fallback;
                    switch (serializerToUse)
                    {
                        case SerializerToUse.IlBasedFallbackSerializer:
                            fallback = typeof(ILBasedSerializer);
                            break;
                        case SerializerToUse.NoFallback:
                            fallback = typeof(SupportsNothingSerializer);
                            break;
                        default:
                            throw new InvalidOperationException("Invalid Serializer was selected");
                    }

                    return SerializationTestEnvironment.InitializeWithDefaults(
                        builder => builder.Configure<SerializationProviderOptions>(
                            options => options.FallbackSerializationProvider = fallback));
                });
        }

        public BuiltInSerializerTests(ITestOutputHelper output, TestEnvironmentFixture fixture, BuiltInSerializerCollectionFixture serializerFixture)
        {
            this.output = output;
            this.defaultFixture = fixture;
            this.serializerFixture = serializerFixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("CodeGen")]
        public void InternalSerializableTypesHaveSerializers()
        {
            var environment = InitializeSerializer(SerializerToUse.NoFallback);
            Assert.True(
                environment.SerializationManager.HasSerializer(typeof(int)),
                $"Should be able to serialize internal type {nameof(Int32)}.");
            Assert.True(
                environment.SerializationManager.HasSerializer(typeof(List<int>)),
                $"Should be able to serialize internal type {nameof(List<int>)}.");
            var grainReferenceType = typeof(IRemindable).Assembly.GetType(
                "Orleans.OrleansCodeGenRemindableReference",
                throwOnError: true);
            Assert.True(
                environment.SerializationManager.HasSerializer(grainReferenceType),
                $"Should be able to serialize grain reference type {grainReferenceType}.");
            Assert.True(
                environment.SerializationManager.HasSerializer(typeof(PubSubGrainState)),
                $"Should be able to serialize internal type {nameof(PubSubGrainState)}.");
            Assert.True(
                environment.SerializationManager.HasSerializer(typeof(EventHubBatchContainer)),
                $"Should be able to serialize internal type {nameof(EventHubBatchContainer)}.");
            Assert.True(
                environment.SerializationManager.HasSerializer(typeof(EventHubSequenceTokenV2)),
                $"Should be able to serialize internal type {nameof(EventHubSequenceTokenV2)}.");
        }

        [Fact(Skip = "See https://github.com/dotnet/orleans/issues/3531"), TestCategory("BVT"), TestCategory("CodeGen")]
        public void ValueTupleTypesHasSerializer()
        {
            var environment = InitializeSerializer(SerializerToUse.NoFallback);
            Assert.True(
                environment.SerializationManager.HasSerializer(typeof(ValueTuple<int, AddressAndTag>)),
                $"Should be able to serialize internal type {nameof(ValueTuple<int, AddressAndTag>)}.");
        }

        [Fact, TestCategory("BVT")]
        public void Serialize_ComplexClass()
        {
            var environment = defaultFixture;
            var expected = OuterClass.GetPrivateClassInstance();
            expected.Int = 89;
            expected.String = Guid.NewGuid().ToString();
            expected.NonSerializedInt = 39;
            expected.Classes = new SomeAbstractClass[]
            {
                expected,
                new AnotherConcreteClass
                {
                    AnotherString = "hi",
                    Interfaces = new List<ISomeInterface> { expected }
                }
            };
            expected.Enum = SomeAbstractClass.SomeEnum.Something;
            expected.SetObsoleteInt(38);

            var actual = (SomeAbstractClass)OrleansSerializationLoop(environment.SerializationManager, expected);

            Assert.Equal(expected.Int, actual.Int);
            Assert.Equal(expected.Enum, actual.Enum);
            Assert.Equal(expected.String, ((OuterClass.SomeConcreteClass)actual).String);
            Assert.Equal(expected.Classes.Length, actual.Classes.Length);
            Assert.Equal(expected.String, ((OuterClass.SomeConcreteClass)actual.Classes[0]).String);
            Assert.Equal(expected.Classes[1].Interfaces[0].Int, actual.Classes[1].Interfaces[0].Int);
            Assert.Equal(0, actual.NonSerializedInt);
            Assert.Equal(expected.GetObsoleteInt(), actual.GetObsoleteInt());
            Assert.Null(actual.SomeGrainReference);
        }

        /// <summary>
        /// Tests that the default (non-fallback) serializer can handle complex classes.
        /// </summary>
        /// <param name="serializerToUse"></param>
        [Theory, TestCategory("BVT")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_ComplexAccessibleClass(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);
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

            var actual = (AnotherConcreteClass)OrleansSerializationLoop(environment.SerializationManager, expected);

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

        [Theory, TestCategory("BVT")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_Type(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            // Test serialization of Type.
            var expected = typeof(int);
            var actual = (Type)OrleansSerializationLoop(environment.SerializationManager, expected);
            Assert.Equal(expected.AssemblyQualifiedName, actual.AssemblyQualifiedName);

            // Test serialization of RuntimeType.
            expected = 8.GetType();
            actual = (Type)OrleansSerializationLoop(environment.SerializationManager, expected);
            Assert.Equal(expected.AssemblyQualifiedName, actual.AssemblyQualifiedName);
        }

        [Theory, TestCategory("BVT")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_ComplexStruct(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            // Test struct serialization.
            var expected = new SomeStruct(10) { Id = Guid.NewGuid(), PublicValue = 6, ValueWithPrivateGetter = 7 };
            expected.SetValueWithPrivateSetter(8);
            expected.SetPrivateValue(9);
            var actual = (SomeStruct)OrleansSerializationLoop(environment.SerializationManager, expected);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.ReadonlyField, actual.ReadonlyField);
            Assert.Equal(expected.PublicValue, actual.PublicValue);
            Assert.Equal(expected.ValueWithPrivateSetter, actual.ValueWithPrivateSetter);
            Assert.Null(actual.SomeGrainReference);
            Assert.Equal(expected.GetPrivateValue(), actual.GetPrivateValue());
            Assert.Equal(expected.GetValueWithPrivateGetter(), actual.GetValueWithPrivateGetter());
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_ActivationAddress(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);
            GrainId grain = LegacyGrainId.NewId();
            var addr = ActivationAddress.GetAddress(null, grain, default);
            object deserialized = OrleansSerializationLoop(environment.SerializationManager, addr, false);
            Assert.IsAssignableFrom<ActivationAddress>(deserialized);
            Assert.Null(((ActivationAddress)deserialized).Activation); //Activation no longer null after copy
            Assert.Null(((ActivationAddress)deserialized).Silo); //Silo no longer null after copy
            Assert.Equal(grain, ((ActivationAddress)deserialized).Grain); //Grain different after copy
            deserialized = OrleansSerializationLoop(environment.SerializationManager, addr);
            Assert.IsAssignableFrom<ActivationAddress>(deserialized); //ActivationAddress full serialization loop as wrong type
            Assert.Null(((ActivationAddress)deserialized).Activation); //Activation no longer null after full serialization loop
            Assert.Null(((ActivationAddress)deserialized).Silo); //Silo no longer null after full serialization loop
            Assert.Equal(grain, ((ActivationAddress)deserialized).Grain); //Grain different after copy
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_EmptyList(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);
            var list = new List<int>();
            var deserialized = OrleansSerializationLoop(environment.SerializationManager, list, false);
            Assert.IsAssignableFrom<List<int>>(deserialized);  //Empty list of integers copied as wrong type"
            ValidateList(list, (List<int>)deserialized, "int (empty, copy)");
            deserialized = OrleansSerializationLoop(environment.SerializationManager, list);
            Assert.IsAssignableFrom<List<int>>(deserialized); //Empty list of integers full serialization loop as wrong type
            ValidateList(list, (List<int>)deserialized, "int (empty)");
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_BasicDictionaries(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            Dictionary<string, string> source1 = new Dictionary<string, string>();
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            var deserialized = OrleansSerializationLoop(environment.SerializationManager, source1);
            ValidateDictionary<string, string>(source1, deserialized, "string/string");

            Dictionary<int, DateTime> source2 = new Dictionary<int, DateTime>();
            source2[3] = DateTime.Now;
            source2[27] = DateTime.Now.AddHours(2);
            deserialized = OrleansSerializationLoop(environment.SerializationManager, source2);
            ValidateDictionary<int, DateTime>(source2, deserialized, "int/date");
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_ReadOnlyDictionary(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            Dictionary<string, string> source1 = new Dictionary<string, string>();
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            var readOnlySource1 = new ReadOnlyDictionary<string, string>(source1);
            var deserialized = OrleansSerializationLoop(environment.SerializationManager, readOnlySource1);
            ValidateReadOnlyDictionary(readOnlySource1, deserialized, "string/string");

            Dictionary<int, DateTime> source2 = new Dictionary<int, DateTime>();
            source2[3] = DateTime.Now;
            source2[27] = DateTime.Now.AddHours(2);
            var readOnlySource2 = new ReadOnlyDictionary<int, DateTime>(source2);
            deserialized = OrleansSerializationLoop(environment.SerializationManager, readOnlySource2);
            ValidateReadOnlyDictionary(readOnlySource2, deserialized, "int/date");
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_DictionaryWithComparer(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            Dictionary<string, string> source1 = new Dictionary<string, string>(new CaseInsensitiveStringEquality());
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            var deserialized = OrleansSerializationLoop(environment.SerializationManager, source1);
            ValidateDictionary<string, string>(source1, deserialized, "case-insensitive string/string");
            Dictionary<string, string> result1 = deserialized as Dictionary<string, string>;
            Assert.Equal(source1["Hello"], result1["hElLo"]); //Round trip for case insensitive string/string dictionary lost the custom comparer

            Dictionary<int, DateTime> source2 = new Dictionary<int, DateTime>(new Mod5IntegerComparer());
            source2[3] = DateTime.Now;
            source2[27] = DateTime.Now.AddHours(2);
            deserialized = OrleansSerializationLoop(environment.SerializationManager, source2);
            ValidateDictionary<int, DateTime>(source2, deserialized, "int/date");
            Dictionary<int, DateTime> result2 = (Dictionary<int, DateTime>)deserialized;
            Assert.Equal<DateTime>(source2[3], result2[13]);  //Round trip for case insensitive int/DateTime dictionary lost the custom comparer"
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_SortedDictionaryWithComparer(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            var source1 = new SortedDictionary<string, string>(new CaseInsensitiveStringComparer());
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            object deserialized = OrleansSerializationLoop(environment.SerializationManager, source1);
            ValidateSortedDictionary<string, string>(source1, deserialized, "string/string");
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_SortedListWithComparer(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            var source1 = new SortedList<string, string>(new CaseInsensitiveStringComparer());
            source1["Hello"] = "Yes";
            source1["Goodbye"] = "No";
            object deserialized = OrleansSerializationLoop(environment.SerializationManager, source1);
            ValidateSortedList<string, string>(source1, deserialized, "string/string");
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_HashSetWithComparer(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            var source1 = new HashSet<string>(new CaseInsensitiveStringEquality());
            source1.Add("one");
            source1.Add("two");
            source1.Add("three");
            var deserialized = OrleansSerializationLoop(environment.SerializationManager, source1);
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

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_Stack(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            var source1 = new Stack<string>();
            source1.Push("one");
            source1.Push("two");
            source1.Push("three");
            object deserialized = OrleansSerializationLoop(environment.SerializationManager, source1);
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
        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_TypeWithOnDeserializedHook(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            var input = new TypeWithOnDeserializedHook
            {
                Int = 5
            };
            var deserialized = OrleansSerializationLoop(environment.SerializationManager, input);
            var result = Assert.IsType<TypeWithOnDeserializedHook>(deserialized);
            Assert.Equal(input.Int, result.Int);
            Assert.Null(input.Context);
            Assert.NotNull(result.Context);
            Assert.Equal(environment.SerializationManager, result.Context.GetSerializationManager());
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_SortedSetWithComparer(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            var source1 = new SortedSet<string>(new CaseInsensitiveStringComparer());
            source1.Add("one");
            source1.Add("two");
            source1.Add("three");
            object deserialized = OrleansSerializationLoop(environment.SerializationManager, source1);
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

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_Array(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            var source1 = new int[] { 1, 3, 5 };
            object deserialized = OrleansSerializationLoop(environment.SerializationManager, source1);
            ValidateArray<int>(source1, deserialized, "int");

            var source2 = new string[] { "hello", "goodbye", "yes", "no", "", "I don't know" };
            deserialized = OrleansSerializationLoop(environment.SerializationManager, source2);
            ValidateArray<string>(source2, deserialized, "string");

            var source3 = new sbyte[] { 1, 3, 5 };
            deserialized = OrleansSerializationLoop(environment.SerializationManager, source3);
            ValidateArray<sbyte>(source3, deserialized, "sbyte");

            var source4 = new byte[] { 1, 3, 5 };
            deserialized = OrleansSerializationLoop(environment.SerializationManager, source4);
            ValidateArray<byte>(source4, deserialized, "byte");

            var source5 = Enumerable.Repeat(3, (environment.SerializationManager.LargeObjectSizeThreshold / sizeof(int)) + 1).ToArray();
            deserialized = OrleansSerializationLoop(environment.SerializationManager, source5);
            ValidateArray<int>(source5, deserialized, "int");
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_ArrayOfArrays(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            var source1 = new[] { new[] { 1, 3, 5 }, new[] { 10, 20, 30 }, new[] { 17, 13, 11, 7, 5, 3, 2 } };
            object deserialized = OrleansSerializationLoop(environment.SerializationManager, source1);
            ValidateArrayOfArrays(source1, deserialized, "int");

            var source2 = new[] { new[] { "hello", "goodbye", "yes", "no", "", "I don't know" }, new[] { "yes" } };
            deserialized = OrleansSerializationLoop(environment.SerializationManager, source2);
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
            deserialized = OrleansSerializationLoop(environment.SerializationManager, source3);
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
            deserialized = OrleansSerializationLoop(environment.SerializationManager, source4);
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
            deserialized = OrleansSerializationLoop(environment.SerializationManager, source5);
            ValidateArrayOfArrays(source5, deserialized, "grain reference (large)");
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_ArrayOfArrayOfArrays(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            var source1 = new[] { new[] { 1, 3, 5 }, new[] { 10, 20, 30 }, new[] { 17, 13, 11, 7, 5, 3, 2 } };
            var source2 = new[] { new[] { 1, 3 }, new[] { 10, 20 }, new[] { 17, 13, 11, 7, 5 } };
            var source3 = new[] { new[] { 1, 3, 5 }, new[] { 10, 20, 30 } };
            var source = new[] { source1, source2, source3 };
            object deserialized = OrleansSerializationLoop(environment.SerializationManager, source);
            ValidateArrayOfArrayOfArrays(source, deserialized, "int");
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_ReadOnlyCollection(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);

            var source1 = new List<string> { "Yes", "No" };
            var collection = new ReadOnlyCollection<string>(source1);
            var deserialized = OrleansSerializationLoop(environment.SerializationManager, collection);
            ValidateReadOnlyCollectionList(collection, deserialized, "string/string");
        }

        private class BanningTypeResolver : ITypeResolver
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

            public Type ResolveType(string name)
            {
                var result = _resolver.ResolveType(name);
                if (_blockedTypes.Contains(result))
                {
                    result = null;
                }

                return result;
            }

            public bool TryResolveType(string name, out Type type)
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
        public void Serialize_UnserializableException()
        {
            const string message = "This is a test message";
            var environment = SerializationTestEnvironment.InitializeWithDefaults(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.Replace(new ServiceDescriptor(typeof(ITypeResolver), new BanningTypeResolver(typeof(UnserializableException))));
                });
            });

            // throw the exception so that stack trace is populated
            Exception source = Assert.Throws<UnserializableException>((Action)(() => { throw new UnserializableException(message); }));
            object deserialized = OrleansSerializationLoop(environment.SerializationManager, source);

            var result = Assert.IsAssignableFrom<UnavailableExceptionFallbackException>(deserialized); //Type is wrong after round trip of unserializable exception
            Assert.Contains(message, result.Message); //Exception message is correct after round trip of unserializable exception
            Assert.Equal(RuntimeTypeNameFormatter.Format(source.GetType()), result.ExceptionType);
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_ObjectIdentity(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);
            var val = new List<string> { "first", "second" };

            var val2 = new List<string> { "first", "second" };

            var source = new Dictionary<string, List<string>>();
            source["one"] = val;
            source["two"] = val;
            source["three"] = val2;
            Assert.Same(source["one"], source["two"]); //Object identity lost before round trip of string/list dict!!!

            var deserialized = OrleansSerializationLoop(environment.SerializationManager, source);
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
            var raw = OrleansSerializationLoop(defaultFixture.SerializationManager, test1, false);
            var result = Assert.IsAssignableFrom<Unrecognized>(raw); //Type is wrong after deep copy of unrecognized
            Assert.Equal(3, result.A);  //Property A is wrong after deep copy of unrecognized"
            Assert.Equal(27, result.B);  //Property B is wrong after deep copy of unrecognized"

            var test2 = new Unrecognized[3];
            for (int i = 0; i < 3; i++)
            {
                test2[i] = new Unrecognized { A = i, B = 2 * i };
            }
            raw = OrleansSerializationLoop(defaultFixture.SerializationManager, test2);
            Assert.IsAssignableFrom<Unrecognized[]>(raw); //Type is wrong after round trip of array of unrecognized
            var result2 = (Unrecognized[])raw;
            Assert.Equal(3, result2.Length); //Array length is wrong after round trip of array of unrecognized
            for (int j = 0; j < 3; j++)
            {
                Assert.Equal(j, result2[j].A); //Property A at index " + j + "is wrong after round trip of array of unrecognized
                Assert.Equal(2 * j, result2[j].B); //Property B at index " + j + "is wrong after round trip of array of unrecognized
            }
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_Immutable(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);
            var test1 = new ImmutableType(3, 27);
            var raw = environment.SerializationManager.DeepCopy(test1);
            Assert.IsAssignableFrom<ImmutableType>(raw); //Type is wrong after deep copy of [Immutable] type
            Assert.Same(test1, raw); //Deep copy of [Immutable] object made a copy instead of just copying the pointer

            var test2list = new List<int>();
            for (int i = 0; i < 3; i++)
            {
                test2list.Add(i);
            }
            var test2 = new Immutable<List<int>>(test2list);
            raw = environment.SerializationManager.DeepCopy(test2);
            Assert.IsAssignableFrom<Immutable<List<int>>>(raw); //Type is wrong after round trip of array of Immutable<>
            Assert.Same(test2.Value, ((Immutable<List<int>>)raw).Value); //Deep copy of Immutable<> object made a copy instead of just copying the pointer

            var test3 = new EmbeddedImmutable("test", 1, 2, 3, 4);
            raw = environment.SerializationManager.DeepCopy(test3);
            Assert.IsAssignableFrom<EmbeddedImmutable>(raw); //Type is wrong after deep copy of type containing an Immutable<> field
            Assert.Same(test3.B.Value, ((EmbeddedImmutable)raw).B.Value); //Deep copy of embedded [Immutable] object made a copy instead of just copying the pointer
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_Uri_Multithreaded(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);
            Parallel.For(0, 50, i =>
            {
                Uri test1 = new Uri("http://www.microsoft.com/" + i);
                object raw = environment.SerializationManager.DeepCopy(test1);
                Assert.IsAssignableFrom<Uri>(raw); //Type is wrong after deep copy of Uri
                Assert.Same(test1, raw); //Deep copy made a copy instead of just copying the pointer

                object deserialized = OrleansSerializationLoop(environment.SerializationManager, test1);
                Assert.IsAssignableFrom<Uri>(deserialized); //Type is wrong after round-trip of Uri
                Uri result = (Uri)deserialized;
                Assert.Equal(test1, result); //Wrong contents after round-trip of Uri
            });
        }

        ////[Fact, TestCategory("Functional")]
        //public void Serialize_RequestInvocationHistory()
        //{
        //    //Message inMsg = new Message();
        //    //inMsg.TargetGrain = LegacyGrainId.NewId();
        //    //inMsg.TargetActivation = ActivationId.NewId();
        //    //inMsg.InterfaceId = 12;
        //    //inMsg.MethodId = 13;

        //    //RequestInvocationHistory src = new RequestInvocationHistory(inMsg);
        //    //inMsg.AddToCallChainHeader(src);

        //    ////object deserialized = OrleansSerializationLoop(environment.SerializationManager, inMsg);
        //    ////Message outMsg = (Message)deserialized;
        //    ////IEnumerable<RequestInvocationHistory> dstArray = outMsg.CallChainHeader;
        //    ////RequestInvocationHistory dst = dstArray.FirstOrDefault();

        //    ////object deserialized = OrleansSerializationLoop(environment.SerializationManager, src);
        //    ////RequestInvocationHistory dst = (RequestInvocationHistory)deserialized;

        //    //Dictionary<string, object> deserialized = SerializeMessage(inMsg);
        //    //IEnumerable<RequestInvocationHistory> dstArray = ((IEnumerable)deserialized[Message.Header.CallChainHeader]).Cast<RequestInvocationHistory>();
        //    //RequestInvocationHistory dst = dstArray.FirstOrDefault();

        //    //Assert.Equal(src.GrainId, dst.GrainId);
        //    //Assert.Equal(src.ActivationId, dst.ActivationId);
        //    //Assert.Equal(src.InterfaceId, dst.InterfaceId);
        //    //Assert.Equal(src.MethodId, dst.MethodId);
        //}

        //private Dictionary<string, object> SerializeMessage(Message msg)
        //{
        //    var outStream = new BinaryTokenStreamWriter();
        //    SerializationManager.SerializeMessageHeaders(msg.headers, outStream);
        //    var inStream = new BinaryTokenStreamReader(outStream.ToByteArray());
        //    var copy = SerializationManager.DeserializeMessageHeaders(inStream);
        //    return copy;
        //}

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_GrainReference(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);
            GrainId grainId = LegacyGrainId.NewId();
            GrainReference input = (GrainReference)environment.InternalGrainFactory.GetGrain(grainId);

            object deserialized = OrleansSerializationLoop(environment.SerializationManager, input);

            var grainRef = Assert.IsAssignableFrom<GrainReference>(deserialized); //GrainReference copied as wrong type
            Assert.Equal(grainId, grainRef.GrainId); //GrainId different after copy
            Assert.Equal(input, grainRef); //Wrong contents after round-trip of input
        }

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_ValidateBuildSegmentListWithLengthLimit(SerializerToUse serializerToUse)
        {
            _ = InitializeSerializer(serializerToUse);
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
            Assert.Equal(0, actual1[0].Offset);
            Assert.Single(actual1[0]);
            Assert.Equal(array1, actual1[0].Array);
            Assert.Equal(0, actual1[1].Offset);
            Assert.Single(actual1[1]);
            Assert.Equal(array2, actual1[1].Array);
            // 2: {2, [3}, {4], 5, 6}
            Assert.Equal(1, actual2[0].Offset);
            Assert.Single(actual2[0]);
            Assert.Equal(array2, actual2[0].Array);
            Assert.Equal(0, actual2[1].Offset);
            Assert.Single(actual2[1]);
            Assert.Equal(array3, actual2[1].Array);
            // 3: {4, [5, 6]}
            Assert.Equal(1, actual3[0].Offset);
            Assert.Equal(2, actual3[0].Count);
            Assert.Equal(array3, actual3[0].Array);
            // 4: {[7, 8], 9, 10}
            Assert.Equal(0, actual4[0].Offset);
            Assert.Equal(2, actual4[0].Count);
            Assert.Equal(array4, actual4[0].Array);
            // 5: {7, 8, [9, 10]}
            Assert.Equal(2, actual5[0].Offset);
            Assert.Equal(2, actual5[0].Count);
            Assert.Equal(array4, actual5[0].Array);
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

        [Theory, TestCategory("Functional")]
        [InlineData(typeof(Dictionary<string, object>))]
        [InlineData(typeof(Dictionary<string, int>))]
        [InlineData(typeof(NonShallowCopyableValueType))]
        [InlineData(typeof(NonShallowCopyableValueType?))]
        [InlineData(typeof(Tuple<string, NonShallowCopyableValueType>))]
        public void SerializationTests_IsNotOrleansShallowCopyable(Type type)
        {
            Assert.False(type.IsOrleansShallowCopyable());
        }

        [Theory, TestCategory("Functional")]
        [InlineData(typeof(int))]
        [InlineData(typeof(DateTime))]
        [InlineData(typeof(Immutable<Dictionary<string, object>>))]
        [InlineData(typeof(ShallowCopyableValueType))]
        [InlineData(typeof(ArgumentNullException))]
        [InlineData(typeof(int?))]
        [InlineData(typeof(Tuple<string, int>))]
        [InlineData(typeof(Tuple<Guid?, Tuple<string, ShallowCopyableValueType?, DateTimeOffset>>))]
        public void SerializationTests_IsOrleansShallowCopyable(Type type)
        {
            Assert.True(type.IsOrleansShallowCopyable());
        }

        public struct ShallowCopyableValueType
        {
            public int AnotherValueType;
        }

        public struct NonShallowCopyableValueType
        {
            public object AutoProp { get; }
            public NonShallowCopyableValueType(object o) => AutoProp = o;
        }

        internal static object OrleansSerializationLoop(SerializationManager serializationManager, object input, bool includeWire = true)
        {
            var copy = serializationManager.DeepCopy(input);
            if (includeWire)
            {
                copy = serializationManager.RoundTripSerializationForTesting(copy);
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

        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_CircularReference(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);
            var c1 = new CircularTest1();
            var c2 = new CircularTest2();
            c2.CircularTest1List.Add(c1);
            c1.CircularTest2 = c2;

            var deserialized = (CircularTest1)OrleansSerializationLoop(environment.SerializationManager, c1);
            Assert.Equal(c1.CircularTest2.CircularTest1List.Count, deserialized.CircularTest2.CircularTest1List.Count);
            Assert.Same(deserialized, deserialized.CircularTest2.CircularTest1List[0]);

            deserialized = (CircularTest1)OrleansSerializationLoop(environment.SerializationManager, c1, true);
            Assert.Equal(c1.CircularTest2.CircularTest1List.Count, deserialized.CircularTest2.CircularTest1List.Count);
            Assert.Same(deserialized, deserialized.CircularTest2.CircularTest1List[0]);
        }
        
        [Theory, TestCategory("Functional")]
        [InlineData(SerializerToUse.NoFallback)]
        public void Serialize_Enums(SerializerToUse serializerToUse)
        {
            var environment = InitializeSerializer(serializerToUse);
            var result = OrleansSerializationLoop(environment.SerializationManager, IntEnum.Value2);
            var typedResult = Assert.IsType<IntEnum>(result);
            Assert.Equal(IntEnum.Value2, typedResult);

            var result2 = OrleansSerializationLoop(environment.SerializationManager, UShortEnum.Value3);
            var typedResult2 = Assert.IsType<UShortEnum>(result2);
            Assert.Equal(UShortEnum.Value3, typedResult2);

            var test = new ClassWithEnumTestData { EnumValue = TestEnum.Third, Enemy = CampaignEnemyTestType.Enemy3 };
            var result3 = OrleansSerializationLoop(environment.SerializationManager, test);
            var typedResult3 = Assert.IsType<ClassWithEnumTestData>(result3);

            Assert.Equal(TestEnum.Third, typedResult3.EnumValue);
            Assert.Equal(CampaignEnemyTestType.Enemy3, typedResult3.Enemy);

            var result4 = OrleansSerializationLoop(environment.SerializationManager, CampaignEnemyType.Enemy3);
            var typedResult4 = Assert.IsType<CampaignEnemyType>(result4);
            Assert.Equal(CampaignEnemyType.Enemy3, typedResult4);
        }

        public class SupportsNothingSerializer : IExternalSerializer
        {
            public bool IsSupportedType(Type itemType) => false;

            public object DeepCopy(object source, ICopyContext context)
            {
                throw new NotSupportedException();
            }

            public void Serialize(object item, ISerializationContext context, Type expectedType)
            {
                throw new NotSupportedException();
            }

            public object Deserialize(Type expectedType, IDeserializationContext context)
            {
                throw new NotSupportedException();
            }
        }
        
        /// <summary>
        /// Tests that ISerializable classes have their callbacks called in the correct order.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public void ISerializable_CallbackOrder_Class()
        {
            var environment = InitializeSerializer(SerializerToUse.Default);
            var input = new SimpleISerializableObject
            {
                Payload = "pyjamas"
            };

            // Verify that our behavior conforms to our expected behavior.
            var result = (SimpleISerializableObject)OrleansSerializationLoop(environment.SerializationManager, input);
            Assert.Equal(
                new[]
                {
                    "default_ctor",
                    "serializing",
                    "serialized"
                },
                input.History);
            Assert.Equal(3, input.Contexts.Count);
            Assert.All(input.Contexts, ctx => Assert.True(ctx.Context is ICopyContext || ctx.Context is ISerializationContext));

            Assert.Equal(
                new[]
                {
                    "deserializing",
                    "serialization_ctor",
                    "deserialized",
                    "deserialization"
                },
                result.History);
            Assert.Equal(input.Payload, result.Payload, StringComparer.Ordinal);
            Assert.Equal(3, result.Contexts.Count);
            Assert.All(result.Contexts, ctx => Assert.True(ctx.Context is IDeserializationContext));
        }

        /// <summary>
        /// Tests that ISerializable structs have their callbacks called in the correct order.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public void ISerializable_CallbackOrder_Struct()
        {
            var environment = InitializeSerializer(SerializerToUse.Default);
            var input = new SimpleISerializableStruct
            {
                Payload = "pyjamas"
            };

            // Verify that our behavior conforms to our expected behavior.
            var result = (SimpleISerializableStruct)OrleansSerializationLoop(environment.SerializationManager, input);
            Assert.Equal(
                new[]
                {
                    "serialization_ctor",
                    "deserialized",
                    "deserialization"
                },
                result.History);
            Assert.Equal(input.Payload, result.Payload, StringComparer.Ordinal);
            Assert.Equal(2, result.Contexts.Count);
            Assert.All(result.Contexts, ctx => Assert.True(ctx.Context is IDeserializationContext));
        }

        [Serializable]
        private class SimpleISerializableObject : ISerializable, IDeserializationCallback
        {
            private List<string> history;
            private List<StreamingContext> contexts;

            public SimpleISerializableObject()
            {
                this.History.Add("default_ctor");
            }

            public SimpleISerializableObject(SerializationInfo info, StreamingContext context)
            {
                this.History.Add("serialization_ctor");
                this.Contexts.Add(context);
                this.Payload = info.GetString(nameof(this.Payload));
            }

            public List<string> History => this.history ?? (this.history = new List<string>());
            public List<StreamingContext> Contexts => this.contexts ?? (this.contexts = new List<StreamingContext>());

            public string Payload { get; set; }

            public void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                this.Contexts.Add(context);
                info.AddValue(nameof(this.Payload), this.Payload);
            }

            [OnSerializing]
            internal void OnSerializingMethod(StreamingContext context)
            {
                this.History.Add("serializing");
                this.Contexts.Add(context);
            }

            [OnSerialized]
            internal void OnSerializedMethod(StreamingContext context)
            {
                this.History.Add("serialized");
                this.Contexts.Add(context);
            }

            [OnDeserializing]
            internal void OnDeserializingMethod(StreamingContext context)
            {
                this.History.Add("deserializing");
                this.Contexts.Add(context);
            }

            [OnDeserialized]
            internal void OnDeserializedMethod(StreamingContext context)
            {
                this.History.Add("deserialized");
                this.Contexts.Add(context);
            }

            void IDeserializationCallback.OnDeserialization(object sender)
            {
                this.History.Add("deserialization");
            }
        }

        [Serializable]
        private struct SimpleISerializableStruct : ISerializable, IDeserializationCallback
        {
            private List<string> history;
            private List<StreamingContext> contexts;

            public SimpleISerializableStruct(SerializationInfo info, StreamingContext context)
            {
                this.history = null;
                this.contexts = null;
                this.Payload = info.GetString(nameof(this.Payload));
                this.History.Add("serialization_ctor");
                this.Contexts.Add(context);
            }

            public List<string> History => this.history ?? (this.history = new List<string>());
            public List<StreamingContext> Contexts => this.contexts ?? (this.contexts = new List<StreamingContext>());

            public string Payload { get; set; }

            public void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                this.Contexts.Add(context);
                info.AddValue(nameof(this.Payload), this.Payload);
            }

            [OnSerializing]
            internal void OnSerializingMethod(StreamingContext context)
            {
                this.History.Add("serializing");
                this.Contexts.Add(context);
            }

            [OnSerialized]
            internal void OnSerializedMethod(StreamingContext context)
            {
                this.History.Add("serialized");
                this.Contexts.Add(context);
            }

            [OnDeserializing]
            internal void OnDeserializingMethod(StreamingContext context)
            {
                this.History.Add("deserializing");
                this.Contexts.Add(context);
            }

            [OnDeserialized]
            internal void OnDeserializedMethod(StreamingContext context)
            {
                this.History.Add("deserialized");
                this.Contexts.Add(context);
            }

            void IDeserializationCallback.OnDeserialization(object sender)
            {
                this.History.Add("deserialization");
            }
        }
    }
}

// ReSharper restore NotAccessedVariable