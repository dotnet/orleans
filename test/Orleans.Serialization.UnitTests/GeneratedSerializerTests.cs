using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using Xunit;

namespace Orleans.Serialization.UnitTests
{
    [Trait("Category", "BVT")]
    public class GeneratedSerializerTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly CodecProvider _codecProvider;
        private readonly SerializerSessionPool _sessionPool;
        private readonly Serializer _serializer;

        public GeneratedSerializerTests()
        {
            _serviceProvider = new ServiceCollection()
                .AddSerializer()
                .BuildServiceProvider();
            _codecProvider = _serviceProvider.GetRequiredService<CodecProvider>();
            _sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
            _serializer = _serviceProvider.GetRequiredService<Serializer>();
        }

        [Fact]
        public void GeneratedSerializersRoundTripThroughCodec()
        {
            var original = new SomeClassWithSerializers { IntField = 2, IntProperty = 30, OtherObject = MyCustomEnum.Two };
            var result = RoundTripThroughCodec(original);

            Assert.Equal(original.IntField, result.IntField);
            Assert.Equal(original.IntProperty, result.IntProperty);
            var otherObj = Assert.IsType<MyCustomEnum>(result.OtherObject);
            Assert.Equal(MyCustomEnum.Two, otherObj);
        }

        [Fact]
        public void GeneratedRecordSerializersRoundTripThroughCodec()
        {
            var original = new Person(2, "harry")
            {
                FavouriteColor = "redborine",
                StarSign = "Aquaricorn"
            };

            var result = RoundTripThroughCodec(original);

            Assert.Equal(original.Age, result.Age);
            Assert.Equal(original.Name, result.Name);
            Assert.Equal(original.FavouriteColor, result.FavouriteColor);
            Assert.Equal(original.StarSign, result.StarSign);
        }

        [Fact]
        public void RecursiveTypeSerializersRoundTripThroughSerializer()
        {
            var original = new RecursiveClass { IntProperty = 30 };
            original.RecursiveProperty = original;
            var result = (RecursiveClass)RoundTripThroughUntypedSerializer(original, out _);

            Assert.NotNull(result.RecursiveProperty);
            Assert.Same(result, result.RecursiveProperty);
            Assert.Equal(original.IntProperty, result.IntProperty);
        }

        [Fact]
        public void RecursiveTypeSerializersRoundTripThroughCodec()
        {
            var original = new RecursiveClass { IntProperty = 30 };
            original.RecursiveProperty = original;
            var result = RoundTripThroughCodec(original);

            Assert.NotNull(result.RecursiveProperty);
            Assert.Same(result, result.RecursiveProperty);
            Assert.Equal(original.IntProperty, result.IntProperty);
        }

        [Fact]
        public void GeneratedRecordWithPCtorSerializersRoundTripThroughCodec()
        {
            var original = new Person2(2, "harry")
            {
                FavouriteColor = "redborine",
                StarSign = "Aquaricorn"
            };

            var result = RoundTripThroughCodec(original);

            Assert.Equal(original.Age, result.Age);
            Assert.Equal(original.Name, result.Name);
            Assert.Equal(original.FavouriteColor, result.FavouriteColor);
            Assert.Equal(original.StarSign, result.StarSign);
        }

        [Fact]
        public void GeneratedRecordWithExcludedPCtorSerializersRoundTripThroughCodec()
        {
            var original = new Person3(2, "harry")
            {
                FavouriteColor = "redborine",
                StarSign = "Aquaricorn"
            };

            var result = RoundTripThroughCodec(original);

            Assert.Equal(default, result.Age);
            Assert.Equal(default, result.Name);
            Assert.Equal(original.FavouriteColor, result.FavouriteColor);
            Assert.Equal(original.StarSign, result.StarSign);
        }

        [Fact]
        public void GeneratedRecordWithExclusiveCtorSerializersRoundTripThroughCodec()
        {
            var original = new Person4(2, "harry");

            var result = RoundTripThroughCodec(original);

            Assert.Equal(original.Age, result.Age);
            Assert.Equal(original.Name, result.Name);
        }

        /// <summary>
        /// Tests that a record type can be serialized bitwise identically to a regular (non-record) class with the same layout.
        /// </summary>
        [Fact]
        public void RecordSerializedAsRegularClass()
        {
            var original = new Person5(2, "harry") { FavouriteColor = "redborine", StarSign = "Aquaricorn" };

            var result = RoundTripThroughCodec(original);

            Assert.Equal(original.Age, result.Age);
            Assert.Equal(original.Name, result.Name);
            Assert.Equal(original.FavouriteColor, result.FavouriteColor);
            Assert.Equal(original.StarSign, result.StarSign);

            // Note that this only works because we are serializing each object using the "expected type" optimization and
            // therefore omitting the concrete type names.
            var originalAsArray = _serializer.SerializeToArray(original);
            var classVersion = new Person5_Class { Age = 2,  Name = "harry", FavouriteColor = "redborine", StarSign = "Aquaricorn" };
            var classAsArray = _serializer.SerializeToArray(classVersion);
            Assert.Equal(originalAsArray, classAsArray);
        }

        [Fact]
        public void GeneratedSerializersRoundTripThroughSerializer()
        {
            var original = new SomeClassWithSerializers { IntField = 2, IntProperty = 30 };
            var result = (SomeClassWithSerializers)RoundTripThroughUntypedSerializer(original, out _);

            Assert.Equal(original.IntField, result.IntField);
            Assert.Equal(original.IntProperty, result.IntProperty);
        }

        [Fact]
        public void GeneratedSerializersRoundTripThroughSerializer_ImmutableClass()
        {
            var original = new ImmutableClass(30, 2, 88, 99);
            var result = (ImmutableClass)RoundTripThroughUntypedSerializer(original, out _);

            Assert.Equal(original.GetIntField(), result.GetIntField());
            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(0, result.UnmarkedField);
            Assert.Equal(0, result.UnmarkedProperty);
        }

        [Fact]
        public void GeneratedSerializersRoundTripThroughSerializer_ImmutableStruct()
        {
            var original = new ImmutableStruct(30, 2);
             var result = (ImmutableStruct)RoundTripThroughUntypedSerializer(original, out _);

            Assert.Equal(original.GetIntField(), result.GetIntField());
            Assert.Equal(original.IntProperty, result.IntProperty);
        }

        [Fact]
        public void UnmarkedFieldsAreNotSerialized()
        {
            var original = new SomeClassWithSerializers { IntField = 2, IntProperty = 30, UnmarkedField = 12, UnmarkedProperty = 47 };
            var result = RoundTripThroughCodec(original);

            Assert.NotEqual(original.UnmarkedField, result.UnmarkedField);
            Assert.NotEqual(original.UnmarkedProperty, result.UnmarkedProperty);
        }

        [Fact]
        public void GenericPocosCanRoundTrip()
        {
            var original = new GenericPoco<string>
            {
                ArrayField = new[] { "a", "bb", "ccc" },
                Field = Guid.NewGuid().ToString("N")
            };
            var result = (GenericPoco<string>)RoundTripThroughUntypedSerializer(original, out var formattedBitStream);

            Assert.Equal(original.ArrayField, result.ArrayField);
            Assert.Equal(original.Field, result.Field);
            Assert.Contains("gpoco`1", formattedBitStream);
        }

        [Fact]
        public void NestedGenericPocoWithTypeAlias()
        {
            var original = new GenericPoco<GenericPoco<string>>
            {
                Field = new GenericPoco<string>
                {
                    Field = Guid.NewGuid().ToString("N")
                }
            };

            RoundTripThroughUntypedSerializer(original, out var formattedBitStream);
            Assert.Contains("gpoco`1[[gpoco`1[[string]]]]", formattedBitStream);
        }

        [Fact]
        public void ArraysAreSupported()
        {
            var original = new[] { "a", "bb", "ccc" };
            var result = (string[])RoundTripThroughUntypedSerializer(original, out _);

            Assert.Equal(original, result);
        }

        [Fact]
        public void ArraysPocoRoundTrip()
        {
            var original = new ArrayPoco<int>
            {
                Array = new[] { 1, 2, 3 },
                Dim2 = new int[,] { { 1 }, { 2 } },
                Dim3 = new int[,,] { { { 2 } } },
                Dim4 = new int[,,,] { { { { 4 } } } },
                Dim32 = new int[,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,] { { { { { { { { { { { { { { { { { { { { { { { { { { { { { { { { 809 } } } } } } } } } } } } } } } } } } } } } } } } } } } } } } } },
                Jagged = new int[][] { new int[] { 909 } }
            };
            var result = (ArrayPoco<int>)RoundTripThroughUntypedSerializer(original, out _);

            Assert.Equal(JsonConvert.SerializeObject(original), JsonConvert.SerializeObject(result));
        }

        [Fact]
        public void MultiDimensionalArraysAreSupported()
        {
            var array2d = new string[,] { { "1", "2", "3" }, { "4", "5", "6" }, { "7", "8", "9" } };
            var result2d = (string[,])RoundTripThroughUntypedSerializer(array2d, out _);

            Assert.Equal(array2d, result2d);
            var array3d = new string[,,]
            {
                { { "b", "b", "4" }, { "a", "g", "a" }, { "a", "g", "p" } },
                { { "g", "r", "g" }, { "1", "3", "a" }, { "l", "k", "a" } },
                { { "z", "b", "g" }, { "5", "7", "a" }, { "5", "n", "0" } }
            };
            var result3d = (string[,,])RoundTripThroughUntypedSerializer(array3d, out _);

            Assert.Equal(array3d, result3d);
        }

        [Fact]
        public void SystemCollectionsRoundTrip()
        {
            var concurrentQueueField = new ConcurrentQueue<int>();
            concurrentQueueField.Enqueue(4);

            var concurrentQueueProperty = new ConcurrentQueue<int>();
            concurrentQueueProperty.Enqueue(5);
            concurrentQueueProperty.Enqueue(6);

            var concurrentDictField = new ConcurrentDictionary<string, int>();
            _ = concurrentDictField.TryAdd("nine", 9);

            var concurrentDictProperty = new ConcurrentDictionary<string, int>();
            _ = concurrentDictProperty.TryAdd("ten", 10);
            _ = concurrentDictProperty.TryAdd("eleven", 11);

            var original = new SystemCollectionsClass
            {
                hashSetField = new HashSet<string> { "one" },
                HashSetProperty = new HashSet<string> { "two", "three" },
                concurrentQueueField = concurrentQueueField,
                ConcurrentQueueProperty = concurrentQueueProperty,
                concurrentDictField = concurrentDictField,
                ConcurrentDictProperty = concurrentDictProperty
            };
            var result = RoundTripThroughCodec(original);

            Assert.Equal(original.hashSetField, result.hashSetField);
            Assert.Equal(original.HashSetProperty, result.HashSetProperty);

            Assert.Equal(original.concurrentQueueField, result.concurrentQueueField);
            Assert.Equal(original.ConcurrentQueueProperty, result.ConcurrentQueueProperty);

            // Order of the key-value pairs in the return value may not match the order of the key-value pairs in the surrogate
            Assert.Equal(original.concurrentDictField["nine"], result.concurrentDictField["nine"]);
            Assert.Equal(original.ConcurrentDictProperty["ten"], result.ConcurrentDictProperty["ten"]);
            Assert.Equal(original.ConcurrentDictProperty["eleven"], result.ConcurrentDictProperty["eleven"]);
        }

        [Fact]
        public void ClassWithLargeCollectionAndUriRoundTrip()
        {
            var largeCollection = new List<string>(200);
            for (int i = 0; i < 200; i++)
            {
                largeCollection.Add(i.ToString());
            }

            var original = new ClassWithLargeCollectionAndUri
            {
                LargeCollection = largeCollection,
                Uri = new($"http://www.{Guid.NewGuid()}.com/")
            };

            var result = RoundTripThroughCodec(original);
            Assert.Equal(original.Uri, result.Uri);
        }

        [Fact]
        public void ClassWithManualSerializablePropertyRoundTrip()
        {
            var original = new ClassWithManualSerializableProperty
            {
                GuidProperty = Guid.NewGuid(),
            };

            var result = RoundTripThroughCodec(original);
            Assert.Equal(original.GuidProperty, result.GuidProperty);
            Assert.Equal(original.StringProperty, result.StringProperty);

            var guidValue = Guid.NewGuid();
            original.StringProperty = guidValue.ToString("N");
            result = RoundTripThroughCodec(original);

            Assert.Equal(guidValue, result.GuidProperty);
            Assert.Equal(original.GuidProperty, result.GuidProperty);

            Assert.Equal(guidValue.ToString("N"), result.StringProperty);
            Assert.Equal(original.StringProperty, result.StringProperty);

            original.StringProperty = "bananas";
            result = RoundTripThroughCodec(original);
 
            Assert.Equal(default(Guid), result.GuidProperty);
            Assert.Equal(original.GuidProperty, result.GuidProperty);
            Assert.Equal("bananas", result.StringProperty);
        }

        [Fact]
        public void ImmutableClassWithImplicitFieldIdsRoundTrip()
        {
            var original = new ClassWithImplicitFieldIds("apples", MyCustomEnum.One);
            var result = RoundTripThroughCodec(original);

            Assert.Equal(original.StringValue, result.StringValue);
            Assert.Equal(original.EnumValue, result.EnumValue);
        }

        [Fact]
        public void CopyImmutableAndSealedTypes()
        {
            DoTest<MyImmutableSub, MyImmutableBase>(new MyImmutableSub { BaseValue = 1, SubValue = 2 });
            DoTest<MyMutableSub, MyImmutableBase>(new MyMutableSub { BaseValue = 1, SubValue = 2 });
            DoTest<MySealedSub, MyMutableBase>(new MySealedSub { BaseValue = 1, SubValue = 2 });
            DoTest<MySealedImmutableSub, MyMutableBase>(new MySealedImmutableSub { BaseValue = 1, SubValue = 2 });
            DoTest<MyUnsealedImmutableSub, MyMutableBase>(new MyUnsealedImmutableSub { BaseValue = 1, SubValue = 2 });

            void DoTest<T, TBase>(T original) where T : TBase, IMySub
            {
                var res1 = Copy(original);
                Assert.Equal(original.BaseValue, res1.BaseValue);
                Assert.Equal(original.SubValue, res1.SubValue);

                var res2 = Assert.IsType<T>(Copy<object>(original));
                Assert.Equal(original.BaseValue, res2.BaseValue);
                Assert.Equal(original.SubValue, res2.SubValue);

                var res3 = Assert.IsType<T>(Copy<TBase>(original));
                Assert.Equal(original.BaseValue, res3.BaseValue);
                Assert.Equal(original.SubValue, res3.SubValue);
            }
        }

        [Fact]
        public void TypeReferencesAreEncodedOnce()
        {
            var original = new object[] { new MyValue(1), new MyValue(2), new MyValue(3) };
            var result = (object[])RoundTripThroughUntypedSerializer(original, out var formattedBitStream);

            Assert.Equal(original, result);
            Assert.Contains("SchemaType: Referenced RuntimeType: MyValue", formattedBitStream);
        }

        [Fact]
        public void TypeCodecDoesNotUpdateTypeReferences()
        {
            var original = new ClassWithTypeFields { Type1 = typeof(MyValue), UntypedValue = new MyValue(42), Type2 = typeof(MyValue) };
            var result = (ClassWithTypeFields)RoundTripThroughUntypedSerializer(original, out var formattedBitStream);

            Assert.Equal(original.Type1, result.Type1);
            Assert.Equal(original.UntypedValue, result.UntypedValue);
            Assert.Equal(original.Type2, result.Type2);

            Assert.Contains("[#4 LengthPrefixed Id: 1 SchemaType: Expected]", formattedBitStream); // Type1
            Assert.Contains("[#5 TagDelimited Id: 2 SchemaType: Encoded RuntimeType: MyValue", formattedBitStream); // UntypedValue
            Assert.Contains("[#7 Reference Id: 3 SchemaType: Expected", formattedBitStream); // Type2
        }

        [Fact]
        public void TypeCodecConsumesTypeReferences()
        {
            var original = new ClassWithTypeFields { UntypedValue = new MyValue(42), Type2 = typeof(MyValue) };
            var result = (ClassWithTypeFields)RoundTripThroughUntypedSerializer(original, out var formattedBitStream);

            Assert.Equal(original.Type1, result.Type1);
            Assert.Equal(original.UntypedValue, result.UntypedValue);
            Assert.Equal(original.Type2, result.Type2);

            Assert.Contains("[#2 Reference Id: 1 SchemaType: Expected Reference: 0]", formattedBitStream); // Type1
            Assert.Contains("[#3 TagDelimited Id: 2 SchemaType: Encoded RuntimeType: MyValue", formattedBitStream); // UntypedValue
            Assert.Contains("[#5 TagDelimited Id: 3 SchemaType: Expected]", formattedBitStream); // Type2
            Assert.Contains("[#7 VarInt Id: 2 SchemaType: Expected] Value: 2", formattedBitStream); // type reference from Type2 field pointing to the encoded field type of UntypedValue
        }

        public void Dispose() => _serviceProvider?.Dispose();

        private T RoundTripThroughCodec<T>(T original)
        {
            T result;
            var pipe = new Pipe();
            using (var readerSession = _sessionPool.GetSession())
            using (var writeSession = _sessionPool.GetSession())
            {
                var writer = Writer.Create(pipe.Writer, writeSession);
                var codec = _codecProvider.GetCodec<T>();
                codec.WriteField(
                    ref writer,
                    0,
                    null,
                    original);
                writer.Commit();
                _ = pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                pipe.Writer.Complete();

                _ = pipe.Reader.TryRead(out var readResult);
                var reader = Reader.Create(readResult.Buffer, readerSession);

                var previousPos = reader.Position;
                var initialHeader = reader.ReadFieldHeader();
                Assert.True(reader.Position > previousPos);

                result = codec.ReadValue(ref reader, initialHeader);
                pipe.Reader.AdvanceTo(readResult.Buffer.End);
                pipe.Reader.Complete();
            }

            return result;
        }

        private T Copy<T>(T original)
        {
            var copier = _serviceProvider.GetRequiredService<DeepCopier<T>>();
            return copier.Copy(original);
        }

        private object RoundTripThroughUntypedSerializer(object original, out string formattedBitStream)
        {
            var pipe = new Pipe();
            object result;
            using (var readerSession = _sessionPool.GetSession())
            using (var writeSession = _sessionPool.GetSession())
            {
                var writer = Writer.Create(pipe.Writer, writeSession);
                var serializer = _serviceProvider.GetService<Serializer<object>>();
                serializer.Serialize(original, ref writer);

                _ = pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                pipe.Writer.Complete();

                _ = pipe.Reader.TryRead(out var readResult);

                using var analyzerSession = _sessionPool.GetSession();
                formattedBitStream = BitStreamFormatter.Format(readResult.Buffer, analyzerSession);

                var reader = Reader.Create(readResult.Buffer, readerSession);

                result = serializer.Deserialize(ref reader);
                pipe.Reader.AdvanceTo(readResult.Buffer.End);
                pipe.Reader.Complete();
            }

            return result;
        }
    }
}