using Microsoft.FSharp.Core;
using Orleans.Serialization;
using TestExtensions;
using UnitTests.FSharpTypes;
using Xunit;

namespace UnitTests.Serialization
{
    /// <summary>
    /// Summary description for SerializationTests
    /// </summary>
    public class SerializationTestsFsharpTypes
    {
        public SerializationTestsFsharpTypes()
        {
            SerializationTestEnvironment.Initialize();
        }

        void RoundtripSerializationTest<T>(T input)
        {
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.Equal(input, output);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_IntOption_Some()
        {
            RoundtripSerializationTest(FSharpOption<int>.Some(10));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_IntOption_None()
        {
            RoundtripSerializationTest(FSharpOption<int>.None);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_Record_ofInt()
        {
            RoundtripSerializationTest(FSharpTypes.Record.ofInt(1));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_Record_ofIntOption_Some()
        {
            RoundtripSerializationTest(RecordOfIntOption.ofInt(1));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_Record_ofIntOption_None()
        {
            RoundtripSerializationTest(RecordOfIntOption.Empty);
        }
    }
}
