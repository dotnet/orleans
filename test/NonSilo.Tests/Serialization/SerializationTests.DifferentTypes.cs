using System.Globalization;
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

            DateTime outputLocal = this.fixture.Serializer.RoundTripSerializationForTesting(inputLocal);
            Assert.Equal(inputLocal.ToString(CultureInfo.InvariantCulture), outputLocal.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(inputLocal.Kind, outputLocal.Kind);

            // UTC Kind
            DateTime inputUtc = DateTime.UtcNow;

            DateTime outputUtc = this.fixture.Serializer.RoundTripSerializationForTesting(inputUtc);
            Assert.Equal(inputUtc.ToString(CultureInfo.InvariantCulture), outputUtc.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(inputUtc.Kind, outputUtc.Kind);

            // Unspecified Kind
            DateTime inputUnspecified = new DateTime(0x08d27e2c0cc7dfb9);

            DateTime outputUnspecified = this.fixture.Serializer.RoundTripSerializationForTesting(inputUnspecified);
            Assert.Equal(inputUnspecified.ToString(CultureInfo.InvariantCulture), outputUnspecified.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(inputUnspecified.Kind, outputUnspecified.Kind);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_DateTimeOffset()
        {
            // Local Kind
            DateTime inputLocalDateTime = DateTime.Now;
            DateTimeOffset inputLocal = new DateTimeOffset(inputLocalDateTime);

            DateTimeOffset outputLocal = this.fixture.Serializer.RoundTripSerializationForTesting(inputLocal);
            Assert.Equal(inputLocal, outputLocal);
            Assert.Equal(
                inputLocal.ToString(CultureInfo.InvariantCulture),
                outputLocal.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(inputLocal.DateTime.Kind, outputLocal.DateTime.Kind);

            // UTC Kind
            DateTime inputUtcDateTime = DateTime.UtcNow;
            DateTimeOffset inputUtc = new DateTimeOffset(inputUtcDateTime);

            DateTimeOffset outputUtc = this.fixture.Serializer.RoundTripSerializationForTesting(inputUtc);
            Assert.Equal(inputUtc, outputUtc);
            Assert.Equal(
                inputUtc.ToString(CultureInfo.InvariantCulture),
                outputUtc.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(inputUtc.DateTime.Kind, outputUtc.DateTime.Kind);

            // Unspecified Kind
            DateTime inputUnspecifiedDateTime = new DateTime(0x08d27e2c0cc7dfb9);
            DateTimeOffset inputUnspecified = new DateTimeOffset(inputUnspecifiedDateTime);

            DateTimeOffset outputUnspecified = this.fixture.Serializer.Deserialize<DateTimeOffset>(this.fixture.Serializer.SerializeToArray(inputUnspecified));
            Assert.Equal(inputUnspecified, outputUnspecified);
            Assert.Equal(
                inputUnspecified.ToString(CultureInfo.InvariantCulture),
                outputUnspecified.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(inputUnspecified.DateTime.Kind, outputUnspecified.DateTime.Kind);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_RecursiveSerialization()
        {
            TestTypeA input = new TestTypeA();
            input.Collection = new HashSet<TestTypeA>();
            input.Collection.Add(input);
            _ = this.fixture.Serializer.RoundTripSerializationForTesting(input);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_CultureInfo()
        {
            var input = new List<CultureInfo> { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("de") };

            foreach (var cultureInfo in input)
            {
                var output = this.fixture.Serializer.RoundTripSerializationForTesting(cultureInfo);
                Assert.Equal(cultureInfo, output);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_CultureInfoList()
        {
            var input = new List<CultureInfo> { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("de") };

            var output = this.fixture.Serializer.RoundTripSerializationForTesting(input);
            Assert.True(input.SequenceEqual(output));
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple()
        {
            var input = new List<ValueTuple<int>> { ValueTuple.Create(1), ValueTuple.Create(100) };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.Serializer.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple2()
        {
            var input = new List<ValueTuple<int, int>> { ValueTuple.Create(1, 2), ValueTuple.Create(100, 200) };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.Serializer.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple3()
        {
            var input = new List<ValueTuple<int, int, int>>
            {
                ValueTuple.Create(1, 2, 3),
                ValueTuple.Create(100, 200, 300)
            };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.Serializer.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple4()
        {
            var input = new List<ValueTuple<int, int, int, int>>
            {
                ValueTuple.Create(1, 2, 3, 4),
                ValueTuple.Create(100, 200, 300, 400)
            };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.Serializer.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple5()
        {
            var input = new List<ValueTuple<int, int, int, int, int>>
            {
                ValueTuple.Create(1, 2, 3, 4, 5),
                ValueTuple.Create(100, 200, 300, 400, 500)
            };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.Serializer.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple6()
        {
            var input = new List<ValueTuple<int, int, int, int, int, int>>
            {
                ValueTuple.Create(1, 2, 3, 4, 5, 6),
                ValueTuple.Create(100, 200, 300, 400, 500, 600)
            };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.Serializer.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple7()
        {
            var input = new List<ValueTuple<int, int, int, int, int, int, int>>
            {
                ValueTuple.Create(1, 2, 3, 4, 5, 6, 7),
                ValueTuple.Create(100, 200, 300, 400, 500, 600, 700)
            };

            foreach (var valueTuple in input)
            {
                var output = this.fixture.Serializer.RoundTripSerializationForTesting(valueTuple);
                Assert.Equal(valueTuple, output);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationTests_ValueTuple8()
        {
            var valueTuple = ValueTuple.Create(1, 2, 3, 4, 5, 6, 7, 8);
            var output = this.fixture.Serializer.RoundTripSerializationForTesting(valueTuple);
            Assert.Equal(valueTuple, output);
        }
    }

    public static class SerializerExtensions
    {
        public static T RoundTripSerializationForTesting<T>(this Serializer serializer, T value) => serializer.Deserialize<T>(serializer.SerializeToArray(value));
    }
}