using Microsoft.FSharp.Core;
using TestExtensions;
using UnitTests.FSharpTypes;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests for Orleans' interoperability with F# types and grains.
    /// Validates that Orleans can correctly serialize/deserialize F# constructs like records, options, and discriminated unions,
    /// ensuring proper cross-language support within the .NET ecosystem.
    /// </summary>
    public class FSharpGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        public FSharpGrainTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        private async Task PingTest<T>(T input)
        {
            var id = Guid.NewGuid();
            var grain = this.GrainFactory.GetGrain<IGeneric1Argument<T>>(id, "UnitTests.Grains.Generic1ArgumentGrain");
            var output = await grain.Ping(input);
            Assert.Equal(input, output);
        }

        /// <summary>
        /// Tests basic generic grain functionality with primitive F# types.
        /// Validates that generic grains can handle simple type parameters when called from C#.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp")]
        public async Task FSharpGrains_Ping()
        {
            await PingTest<int>(1);
        }

        /// <summary>
        /// Tests serialization of basic F# record types.
        /// Validates that Orleans can properly serialize/deserialize F# records containing primitive values.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_Record_ofInt()
        {
            await PingTest<UnitTests.FSharpTypes.Record>(UnitTests.FSharpTypes.Record.ofInt(1));
        }

        /// <summary>
        /// Tests F# record serialization without Orleans attributes.
        /// This test demonstrates Orleans' ability to handle F# records even when they lack
        /// explicit Serializable and Immutable attributes, validating attribute-free serialization.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_Record_ofIntOption_Some_WithNoAttributes()
        {
            await PingTest<RecordOfIntOptionWithNoAttributes>(RecordOfIntOptionWithNoAttributes.ofInt(1));
        }

        /// <summary>
        /// Tests F# record serialization with explicit Orleans attributes.
        /// Validates that F# records containing option types (Some case) can be properly serialized
        /// when decorated with Serializable and Immutable attributes.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_Record_ofIntOption_Some()
        {
            await PingTest<RecordOfIntOption>(RecordOfIntOption.ofInt(1));
        }

        /// <summary>
        /// Tests F# record serialization with None option values.
        /// Validates Orleans' handling of F# option types in the None case, ensuring proper null handling.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_Record_ofIntOption_None()
        {
            await PingTest<RecordOfIntOption>(RecordOfIntOption.Empty);
        }

        /// <summary>
        /// Tests serialization of generic F# record types.
        /// Validates that Orleans can handle F# records with type parameters, testing the combination
        /// of F# generics and Orleans' serialization system.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_GenericRecord_ofInt()
        {
            var input = GenericRecord<int>.ofT(0);
            await PingTest<GenericRecord<int>>(input);
        }

        /// <summary>
        /// Tests serialization of generic F# records containing option types (Some case).
        /// This complex scenario validates Orleans' ability to handle nested F# type constructs:
        /// generic records containing option types with values.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_GenericRecord_ofIntOption_Some()
        {
            var input = GenericRecord<FSharpOption<int>>.ofT(FSharpOption<int>.Some(0));
            await PingTest<GenericRecord<FSharpOption<int>>>(input);
        }

        /// <summary>
        /// Tests serialization of generic F# records containing option types (None case).
        /// Validates proper handling of null values within generic F# record structures.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_GenericRecord_ofIntOption_None()
        {
            var input = GenericRecord<FSharpOption<int>>.ofT(FSharpOption<int>.None);
            await PingTest<GenericRecord<FSharpOption<int>>>(input);
        }

        /// <summary>
        /// Tests direct serialization of F# option types (Some case).
        /// Validates that Orleans can handle F#'s built-in option type when it contains a value.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_IntOption_Some()
        {
            var input = FSharpOption<int>.Some(10);
            await PingTest<FSharpOption<int>>(input);
        }

        /// <summary>
        /// Tests direct serialization of F# option types (None case).
        /// Validates that Orleans correctly handles F#'s None value, which represents the absence of a value.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_IntOption_None()
        {
            var input = FSharpOption<int>.None;
            await PingTest<FSharpOption<int>>(input);
        }
    }
}
