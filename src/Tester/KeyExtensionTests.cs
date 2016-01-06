using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.General
{
    [TestClass]
    public class KeyExtensionTests : UnitTestSiloHost
    {
        [TestCleanup]
        public void TestCleanup()
        {
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
        }

        [TestMethod, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
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

        [TestMethod, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
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

        [TestMethod, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        [ExpectedException(typeof(ArgumentException))]
        public void EmptyKeyExtensionsAreDisallowed()
        {
            var baseKey = Guid.NewGuid();

            GrainClient.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, "", null);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        [ExpectedException(typeof(ArgumentException))]
        public void WhiteSpaceKeyExtensionsAreDisallowed()
        {
            var baseKey = Guid.NewGuid();

            GrainClient.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, " \t\n\r", null);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        [ExpectedException(typeof(ArgumentNullException))]
        public void NullKeyExtensionsAreDisallowed()
        {
            var baseKey = Guid.NewGuid();

            GrainClient.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, null, null);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public async Task PrimaryKeyExtensionsShouldPermitStringsLongerThan127BytesLong()
        {
            var baseKey = Guid.NewGuid();

            string kx1 = new string('\\', 300);

            var localGrainRef = GrainClient.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, kx1, null);
            var remoteGrainRef = await localGrainRef.GetGrainReference();

            Assert.AreEqual(localGrainRef, remoteGrainRef, "Mismatched grain ID.");
        }
    }
}
