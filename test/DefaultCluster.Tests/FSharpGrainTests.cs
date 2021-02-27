using System;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TestExtensions;
using UnitTests.FSharpTypes;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
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

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp")]
        public async Task FSharpGrains_Ping()
        {
            await PingTest<int>(1);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_Record_ofInt()
        {
            await PingTest<UnitTests.FSharpTypes.Record>(UnitTests.FSharpTypes.Record.ofInt(1));
        }

        /// F# record without Serializable and Immutable attributes applied - yields a more meaningful error message
        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_Record_ofIntOption_Some_WithNoAttributes()
        {
            await PingTest<RecordOfIntOptionWithNoAttributes>(RecordOfIntOptionWithNoAttributes.ofInt(1));
        }

        /// F# record with Serializable and Immutable attributes applied - grain call times out,
        /// Debugging the test reveals the same root cause as for FSharpGrains_Record_ofIntOption_WithNoAttributes
        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_Record_ofIntOption_Some()
        {
            await PingTest<RecordOfIntOption>(RecordOfIntOption.ofInt(1));
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_Record_ofIntOption_None()
        {
            await PingTest<RecordOfIntOption>(RecordOfIntOption.Empty);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_GenericRecord_ofInt()
        {
            var input = GenericRecord<int>.ofT(0);
            await PingTest<GenericRecord<int>>(input);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_GenericRecord_ofIntOption_Some()
        {
            var input = GenericRecord<FSharpOption<int>>.ofT(FSharpOption<int>.Some(0));
            await PingTest<GenericRecord<FSharpOption<int>>>(input);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_GenericRecord_ofIntOption_None()
        {
            var input = GenericRecord<FSharpOption<int>>.ofT(FSharpOption<int>.None);
            await PingTest<GenericRecord<FSharpOption<int>>>(input);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_IntOption_Some()
        {
            var input = FSharpOption<int>.Some(10);
            await PingTest<FSharpOption<int>>(input);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_IntOption_None()
        {
            var input = FSharpOption<int>.None;
            await PingTest<FSharpOption<int>>(input);
        }
    }
}
