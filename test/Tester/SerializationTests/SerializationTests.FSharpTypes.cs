#if !EXCLUDEFSHARP
using Microsoft.FSharp.Core;
#endif
using Orleans.Serialization;
using TestExtensions;
#if !EXCLUDEFSHARP
using UnitTests.FSharpTypes;
#endif
using Xunit;

namespace UnitTests.Serialization
{
    /// <summary>
    /// Summary description for SerializationTests
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class SerializationTestsFSharpTypes
    {
        private readonly TestEnvironmentFixture fixture;

        public SerializationTestsFSharpTypes(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
        }

        void RoundtripSerializationTest<T>(T input)
        {
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.Equal(input, output);
        }

#if !EXCLUDEFSHARP
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
#endif
    }
}
