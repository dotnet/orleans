using Microsoft.FSharp.Core;
using Microsoft.FSharp.Collections;
using Orleans.Serialization;
using TestExtensions;
using UnitTests.FSharpTypes;
using Xunit;
using System.Collections.Generic;
using System;

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
            var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(input);
            Assert.Equal(input, output);
        }

        [Fact, TestCategory("BVT"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_IntOption_Some()
        {
            RoundtripSerializationTest(FSharpOption<int>.Some(10));
        }

        [Fact, TestCategory("BVT"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_IntOption_None()
        {
            RoundtripSerializationTest(FSharpOption<int>.None);
        }

        [Fact, TestCategory("BVT"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_Record_ofInt()
        {
            RoundtripSerializationTest(FSharpTypes.Record.ofInt(1));
        }

        [Fact, TestCategory("BVT"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_Record_ofIntOption_Some()
        {
            RoundtripSerializationTest(RecordOfIntOption.ofInt(1));
        }

        [Fact, TestCategory("BVT"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_Record_ofIntOption_None()
        {
            RoundtripSerializationTest(RecordOfIntOption.Empty);
        }

        [Fact, TestCategory("BVT"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_Single_Case_Union()
        {
            RoundtripSerializationTest(SingleCaseDU.ofInt(1));
        }

        [Fact, TestCategory("BVT"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_Discriminated_Union()
        {

            // discriminated union case with an array field
            RoundtripSerializationTest(DiscriminatedUnion.emptyArray());
            RoundtripSerializationTest(DiscriminatedUnion.nonEmptyArray());

            // discriminated union case with an F# list field
            RoundtripSerializationTest(DiscriminatedUnion.emptyList());
            RoundtripSerializationTest(DiscriminatedUnion.nonEmptyList());

            // discriminated union case with an F# set field
            RoundtripSerializationTest(DiscriminatedUnion.emptySet());
            RoundtripSerializationTest(DiscriminatedUnion.nonEmptySet());

            // discriminated union case with an F# map  field
            RoundtripSerializationTest(DiscriminatedUnion.emptyMap());
            RoundtripSerializationTest(DiscriminatedUnion.nonEmptyMap());

        }

        [Fact, TestCategory("BVT"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_Collections()
        {
            var elements = new List<int>() { 0, 1, 2 };

            var mapElements = new List<Tuple<int, string>>(){
                    new Tuple<int, string>(0, "zero"),
                    new Tuple<int, string>(1, "one")
                };

            // F# list
            RoundtripSerializationTest(ListModule.Empty<int>());
            RoundtripSerializationTest(ListModule.OfSeq(elements));

            // F# set
            RoundtripSerializationTest(SetModule.Empty<int>());
            RoundtripSerializationTest(SetModule.OfSeq(elements));

            // F# map
            RoundtripSerializationTest(MapModule.OfSeq(new List<Tuple<int,string>>()));
            RoundtripSerializationTest(MapModule.OfSeq(mapElements));
        }

    }
}
