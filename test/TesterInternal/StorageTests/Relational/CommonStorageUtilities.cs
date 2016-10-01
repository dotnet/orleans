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
        public static void AssertRelationalInconsistentExceptionMessage(string exceptionMessage)
        {
            Assert.Contains("ServiceId=", exceptionMessage);
            Assert.Contains("ProviderName=", exceptionMessage);
            Assert.Contains("GrainType=", exceptionMessage);
            Assert.Contains($"GrainId=", exceptionMessage);
            Assert.Contains($"ETag=", exceptionMessage);
        }


        internal static Tuple<GrainReference, GrainState<TestState1>> GetTestReferenceAndState(long grainId, string version)
        {
            return new Tuple<GrainReference, GrainState<TestState1>>(GrainReference.FromGrainId(GrainId.GetGrainId(UniqueKey.NewKey(grainId, UniqueKey.Category.Grain))), new GrainState<TestState1> { State = new TestState1(), ETag = version });
        }
    }
}
