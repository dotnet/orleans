using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Serialization
{
    /// <summary>
    /// Summary description for SerializationTests
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class SerializationTestsDifferentTypes
    {
        private readonly TestEnvironmentFixture fixture;

        public SerializationTestsDifferentTypes(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_DateTime()
        {
            // Local Kind
            DateTime inputLocal = DateTime.Now;

            DateTime outputLocal = this.fixture.SerializationManager.RoundTripSerializationForTesting(inputLocal);
            Assert.Equal(inputLocal.ToString(CultureInfo.InvariantCulture), outputLocal.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(inputLocal.Kind, outputLocal.Kind);

            // UTC Kind
            DateTime inputUtc = DateTime.UtcNow;

            DateTime outputUtc = this.fixture.SerializationManager.RoundTripSerializationForTesting(inputUtc);
            Assert.Equal(inputUtc.ToString(CultureInfo.InvariantCulture), outputUtc.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(inputUtc.Kind, outputUtc.Kind);

            // Unspecified Kind
            DateTime inputUnspecified = new DateTime(0x08d27e2c0cc7dfb9);

            DateTime outputUnspecified = this.fixture.SerializationManager.RoundTripSerializationForTesting(inputUnspecified);
            Assert.Equal(inputUnspecified.ToString(CultureInfo.InvariantCulture), outputUnspecified.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(inputUnspecified.Kind, outputUnspecified.Kind);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_DateTimeOffset()
        {
            // Local Kind
            DateTime inputLocalDateTime = DateTime.Now;
            DateTimeOffset inputLocal = new DateTimeOffset(inputLocalDateTime);

            DateTimeOffset outputLocal = this.fixture.SerializationManager.RoundTripSerializationForTesting(inputLocal);
            Assert.Equal(inputLocal, outputLocal);
            Assert.Equal(
                inputLocal.ToString(CultureInfo.InvariantCulture),
                outputLocal.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(inputLocal.DateTime.Kind, outputLocal.DateTime.Kind);

            // UTC Kind
            DateTime inputUtcDateTime = DateTime.UtcNow;
            DateTimeOffset inputUtc = new DateTimeOffset(inputUtcDateTime);

            DateTimeOffset outputUtc = this.fixture.SerializationManager.RoundTripSerializationForTesting(inputUtc);
            Assert.Equal(inputUtc, outputUtc);
            Assert.Equal(
                inputUtc.ToString(CultureInfo.InvariantCulture),
                outputUtc.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(inputUtc.DateTime.Kind, outputUtc.DateTime.Kind);

            // Unspecified Kind
            DateTime inputUnspecifiedDateTime = new DateTime(0x08d27e2c0cc7dfb9);
            DateTimeOffset inputUnspecified = new DateTimeOffset(inputUnspecifiedDateTime);

            DateTimeOffset outputUnspecified = this.fixture.SerializationManager.RoundTripSerializationForTesting(inputUnspecified);
            Assert.Equal(inputUnspecified, outputUnspecified);
            Assert.Equal(
                inputUnspecified.ToString(CultureInfo.InvariantCulture),
                outputUnspecified.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(inputUnspecified.DateTime.Kind, outputUnspecified.DateTime.Kind);
        }

        [Serializer(typeof(TestTypeA))]
        internal class TestTypeASerialization
        {
            [CopierMethod]
            public static object DeepCopier(object original, ICopyContext context)
            {
                TestTypeA input = (TestTypeA)original;
                TestTypeA result = new TestTypeA();
                context.RecordCopy(original, result);
                result.Collection = (ICollection<TestTypeA>)SerializationManager.DeepCopyInner(input.Collection, context);
                return result;
            }

            [SerializerMethod]
            public static void Serializer(object untypedInput, ISerializationContext context, Type expected)
            {
                TestTypeA input = (TestTypeA)untypedInput;
                SerializationManager.SerializeInner(input.Collection, context, typeof(ICollection<TestTypeA>));
            }

            [DeserializerMethod]
            public static object Deserializer(Type expected, IDeserializationContext context)
            {
                TestTypeA result = new TestTypeA();
                context.RecordObject(result);
                result.Collection = (ICollection<TestTypeA>)SerializationManager.DeserializeInner(typeof(ICollection<TestTypeA>), context);
                return result;
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_RecursiveSerialization()
        {
            TestTypeA input = new TestTypeA();
            input.Collection = new HashSet<TestTypeA>();
            input.Collection.Add(input);
            _ = this.fixture.SerializationManager.RoundTripSerializationForTesting(input);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_CultureInfo()
        {
            var input = new List<CultureInfo> { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("de") };

            foreach (var cultureInfo in input)
            {
                var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(cultureInfo);
                Assert.Equal(cultureInfo, output);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_CultureInfoList()
        {
            var input = new List<CultureInfo> { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("de") };

            var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(input);
            Assert.True(input.SequenceEqual(output));
        }

        [Fact(Skip = "See https://github.com/dotnet/orleans/issues/3531"), TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple()
        {
            var input = new List<ValueTuple<int>> { ValueTuple.Create(1), ValueTuple.Create(100) };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact(Skip = "See https://github.com/dotnet/orleans/issues/3531"), TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple2()
        {
            var input = new List<ValueTuple<int, int>> { ValueTuple.Create(1, 2), ValueTuple.Create(100, 200) };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact(Skip = "See https://github.com/dotnet/orleans/issues/3531"), TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple3()
        {
            var input = new List<ValueTuple<int, int, int>>
            {
                ValueTuple.Create(1, 2, 3),
                ValueTuple.Create(100, 200, 300)
            };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact(Skip = "See https://github.com/dotnet/orleans/issues/3531"), TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple4()
        {
            var input = new List<ValueTuple<int, int, int, int>>
            {
                ValueTuple.Create(1, 2, 3, 4),
                ValueTuple.Create(100, 200, 300, 400)
            };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact(Skip = "See https://github.com/dotnet/orleans/issues/3531"), TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple5()
        {
            var input = new List<ValueTuple<int, int, int, int, int>>
            {
                ValueTuple.Create(1, 2, 3, 4, 5),
                ValueTuple.Create(100, 200, 300, 400, 500)
            };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact(Skip = "See https://github.com/dotnet/orleans/issues/3531"), TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple6()
        {
            var input = new List<ValueTuple<int, int, int, int, int, int>>
            {
                ValueTuple.Create(1, 2, 3, 4, 5, 6),
                ValueTuple.Create(100, 200, 300, 400, 500, 600)
            };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact(Skip = "See https://github.com/dotnet/orleans/issues/3531"), TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple7()
        {
            var input = new List<ValueTuple<int, int, int, int, int, int, int>>
            {
                ValueTuple.Create(1, 2, 3, 4, 5, 6, 7),
                ValueTuple.Create(100, 200, 300, 400, 500, 600, 700)
            };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact(Skip = "See https://github.com/dotnet/orleans/issues/3531"), TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple8()
        {
            var valueTuple = ValueTuple.Create(1, 2, 3, 4, 5, 6, 7, 8);
            var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(valueTuple);
            Assert.Equal(valueTuple, output);
        }
    }
}