using Orleans;
using Orleans.Runtime;
using System;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;


namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// Common utilities to be used while testing storage providers.
    /// </summary>
    public static class CommonStorageUtilities
    {
        /// <summary>
        /// Asserts certain information is present in the <see cref="Orleans.Storage.InconsistentStateException"/>.
        /// </summary>
        /// <param name="exceptionMessage">The exception message to assert.</param>
        public static void AssertRelationalInconsistentExceptionMessage(string exceptionMessage)
        {
            Assert.Contains("ServiceId=", exceptionMessage);
            Assert.Contains("ProviderName=", exceptionMessage);
            Assert.Contains("GrainType=", exceptionMessage);
            Assert.Contains($"GrainId=", exceptionMessage);
            Assert.Contains($"ETag=", exceptionMessage);
        }


        /// <summary>
        /// Creates a new grain and a grain reference pair.
        /// </summary>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="version">The initial version of the state.</param>
        /// <returns>A grain reference and a state pair.</returns>
        internal static Tuple<GrainReference, GrainState<TestState1>> GetTestReferenceAndState(long grainId, string version)
        {
            return  Tuple.Create(GrainReference.FromGrainId(GrainId.GetGrainId(UniqueKey.NewKey(grainId, UniqueKey.Category.Grain))), new GrainState<TestState1> { State = new TestState1(), ETag = version });
        }


        /// <summary>
        /// Creates a new grain and a grain reference pair.
        /// </summary>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="version">The initial version of the state.</param>
        /// <returns>A grain reference and a state pair.</returns>
        internal static Tuple<GrainReference, GrainState<TestState1>> GetTestReferenceAndState(string grainId, string version)
        {
            return Tuple.Create(GrainReference.FromGrainId(GrainId.FromParsableString(GrainId.GetGrainId(RandomUtilities.NormalGrainTypeCode, grainId).ToParsableString())), new GrainState<TestState1> { State = new TestState1(), ETag = version });
        }
    }
}
