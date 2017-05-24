using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using TestExtensions;
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

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
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

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
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

        [Serializable]
        private class TestTypeA
        {
            public ICollection<TestTypeA> Collection { get; set; }
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

#if !NETSTANDARD_TODO

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_RecursiveSerialization()
        {
            TestTypeA input = new TestTypeA();
            input.Collection = new HashSet<TestTypeA>();
            input.Collection.Add(input);

            TestTypeA output1 = Orleans.TestingHost.Utils.TestingUtils.RoundTripDotNetSerializer(input, this.fixture.GrainFactory, this.fixture.SerializationManager);

            TestTypeA output2 = this.fixture.SerializationManager.RoundTripSerializationForTesting(input);
        }

#endif

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_CultureInfo()
        {
            var input = new List<CultureInfo> { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("de") };

            foreach (var cultureInfo in input)
            {
                var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(cultureInfo);
                Assert.Equal(cultureInfo, output);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_CultureInfoList()
        {
            var input = new List<CultureInfo> { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("de") };

            var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(input);
            Assert.True(input.SequenceEqual(output));
        }
    }
}