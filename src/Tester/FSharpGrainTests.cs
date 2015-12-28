using System;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;

using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using UnitTests.FSharpTypes;

namespace UnitTests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    [TestClass]
    public class FSharpGrainTests : UnitTestSiloHost
    {
        public FSharpGrainTests()
            : base(new TestingSiloOptions { StartPrimary = true, StartSecondary = false })
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        async Task PingTest<T>(T input)
        {
            var id = Guid.NewGuid();
            var grain = GrainFactory.GetGrain<IGeneric1Argument<T>>(id, "UnitTests.Grains.Generic1ArgumentGrain");
            var output = await grain.Ping(input);
            Assert.AreEqual(input, output);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics"), TestCategory("FSharp")]
        public async Task FSharpGrains_Ping()
        {
            await PingTest<int>(1);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_Record_ofInt()
        {
            await PingTest<Record>(Record.ofInt(1));
        }

        /// F# record without Serializable and Immutable attributes applied - yields a more meaningful error message
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_Record_ofIntOption_Some_WithNoAttributes()
        {
            await PingTest<RecordOfIntOptionWithNoAttributes>(RecordOfIntOptionWithNoAttributes.ofInt(1));
        }

        /// F# record with Serializable and Immutable attributes applied - grain call times out,
        /// Debugging the test reveals the same root cause as for FSharpGrains_Record_ofIntOption_WithNoAttributes
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_Record_ofIntOption_Some()
        {
            await PingTest<RecordOfIntOption>(RecordOfIntOption.ofInt(1));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_Record_ofIntOption_None()
        {
            await PingTest<RecordOfIntOption>(RecordOfIntOption.Empty);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_GenericRecord_ofInt()
        {
            var input = GenericRecord<int>.ofT(0);
            await PingTest<GenericRecord<int>>(input);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_GenericRecord_ofIntOption_Some()
        {
            var input = GenericRecord<FSharpOption<int>>.ofT(FSharpOption<int>.Some(0));
            await PingTest< GenericRecord<FSharpOption<int>>>(input);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_GenericRecord_ofIntOption_None()
        {
            var input = GenericRecord<FSharpOption<int>>.ofT(FSharpOption<int>.None);
            await PingTest<GenericRecord<FSharpOption<int>>>(input);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_IntOption_Some()
        {
            var input = FSharpOption<int>.Some(0);
            await PingTest<FSharpOption<int>>(input);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics"), TestCategory("FSharp"), TestCategory("Serialization")]
        public async Task FSharpGrains_Ping_IntOption_None()
        {
            var input = FSharpOption<int>.None;
            await PingTest<FSharpOption<int>>(input);
        }
    }
}
