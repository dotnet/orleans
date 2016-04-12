using System;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using Tester;

namespace UnitTests.General
{
    public class KeyExtensionTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public async Task PrimaryKeyExtensionsShouldDifferentiateGrainsUsingTheSameBasePrimaryKey()
        {
            var baseKey = Guid.NewGuid();

            const string kx1 = "1";
            const string kx2 = "2";

            var grain1 = GrainClient.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, kx1, null);
            var grainId1 = await grain1.GetGrainReference();
            var activationId1 = await grain1.GetActivationId();

            var grain2 = GrainClient.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, kx2, null);
            var grainId2 = await grain2.GetGrainReference();
            var activationId2 = await grain2.GetActivationId();

            Assert.AreNotEqual(
                grainId1,
                grainId2,
                "Mismatched key extensions should differentiate an identical base primary key.");

            Assert.AreNotEqual(
                activationId1,
                activationId2,
                "Mismatched key extensions should differentiate an identical base primary key.");
        }

        [Fact, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public async Task PrimaryKeyExtensionsShouldDifferentiateGrainsUsingDifferentBaseKeys()
        {
            var baseKey1 = Guid.NewGuid();
            var baseKey2 = Guid.NewGuid();

            const string kx = "1";

            var grain1 = GrainClient.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey1, kx, null);
            var grainId1 = await grain1.GetGrainReference();
            var activationId1 = await grain1.GetActivationId();

            var grain2 = GrainClient.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey2, kx, null);
            var grainId2 = await grain2.GetGrainReference();
            var activationId2 = await grain2.GetActivationId();

            Assert.AreNotEqual(
                grainId1,
                grainId2,
                "Mismatched base keys should differentiate between identical extended keys.");

            Assert.AreNotEqual(
                activationId1,
                activationId2,
                "Mismatched base keys should differentiate between identical extended keys.");
        }

        [Fact, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public void EmptyKeyExtensionsAreDisallowed()
        {
            Xunit.Assert.Throws(typeof(ArgumentException), () =>
            {
                var baseKey = Guid.NewGuid();

                GrainClient.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, "", null);
            });
        }

        [Fact, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public void WhiteSpaceKeyExtensionsAreDisallowed()
        {
            Xunit.Assert.Throws(typeof(ArgumentException), () =>
            {
                var baseKey = Guid.NewGuid();

                GrainClient.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, " \t\n\r", null);
            });
        }

        [Fact, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public void NullKeyExtensionsAreDisallowed()
        {
            Xunit.Assert.Throws(typeof(ArgumentNullException), () =>
            {
                var baseKey = Guid.NewGuid();

                GrainClient.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, null, null);
            });
        }

        [Fact, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public async Task PrimaryKeyExtensionsShouldPermitStringsLongerThan127BytesLong()
        {
            var baseKey = Guid.NewGuid();

            string kx1 = new string('\\', 300);

            var localGrainRef = GrainClient.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, kx1, null);
            var remoteGrainRef = await localGrainRef.GetGrainReference();

            Assert.AreEqual(localGrainRef, remoteGrainRef, "Mismatched grain ID.");
        }

        [Fact, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public void GetPrimaryKeyStringOnGrainReference()
        {
            const string key = "foo";

            var grain = GrainClient.GrainFactory.GetGrain<IStringGrain>(key);
            var key2 = ((GrainReference) grain).GetPrimaryKeyString();

            Assert.AreEqual(key, key2, "Unexpected key was returned.");
        }

        [Fact, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public void GetPrimaryKeyStringOnWrongGrainReference()
        {
            var grain = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(0);
            var key = ((GrainReference)grain).GetPrimaryKeyString();
            Assert.IsNull(key);
        }
    }
}
