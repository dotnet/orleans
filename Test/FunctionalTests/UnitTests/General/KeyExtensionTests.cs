using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Orleans.Runtime;

using UnitTestGrainInterfaces;

namespace UnitTests.General
{
    [TestClass]
    public class KeyExtensionTests : UnitTestBase
    {
        [TestCleanup]
        public void TestCleanup()
        {
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("PrimaryKeyExtension")]
        public async Task PrimaryKeyExtensionsShouldDifferentiateGrainsUsingTheSameBasePrimaryKey()
        {
            var baseKey = Guid.NewGuid();

            const string kx1 = "1";
            const string kx2 = "2";

            var grain1 = KeyExtensionTestGrainFactory.GetGrain(baseKey, kx1);
            var grainId1 = await grain1.GetGrainId();
            var activationId1 = await grain1.GetActivationId();

            var grain2 = KeyExtensionTestGrainFactory.GetGrain(baseKey, kx2);
            var grainId2 = await grain2.GetGrainId();
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

        [TestMethod, TestCategory("Nightly"), TestCategory("PrimaryKeyExtension")]
        public async Task PrimaryKeyExtensionsShouldDifferentiateGrainsUsingDifferentBaseKeys()
        {
            var baseKey1 = Guid.NewGuid();
            var baseKey2 = Guid.NewGuid();

            const string kx = "1";

            var grain1 = KeyExtensionTestGrainFactory.GetGrain(baseKey1, kx);
            var grainId1 = await grain1.GetGrainId();
            var activationId1 = await grain1.GetActivationId();

            var grain2 = KeyExtensionTestGrainFactory.GetGrain(baseKey2, kx);
            var grainId2 = await grain2.GetGrainId();
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

        [TestMethod, TestCategory("Nightly"), TestCategory("PrimaryKeyExtension")]
        [ExpectedException(typeof(ArgumentException))]
        public void EmptyKeyExtensionsAreDisallowed()
        {
            var baseKey = Guid.NewGuid();

            KeyExtensionTestGrainFactory.GetGrain(baseKey, "");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("PrimaryKeyExtension")]
        [ExpectedException(typeof(ArgumentException))]
        public void WhiteSpaceKeyExtensionsAreDisallowed()
        {
            var baseKey = Guid.NewGuid();

            KeyExtensionTestGrainFactory.GetGrain(baseKey, " \t\n\r");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("PrimaryKeyExtension")]
        [ExpectedException(typeof(ArgumentNullException))]
        public void NullKeyExtensionsAreDisallowed()
        {
            var baseKey = Guid.NewGuid();

            KeyExtensionTestGrainFactory.GetGrain(baseKey, null);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("PrimaryKeyExtension")]
        public async Task PrimaryKeyExtensionsShouldPermitStringsLongerThan127BytesLong()
        {
            var baseKey = Guid.NewGuid();

            string kx1 = new string('\\', 300);

            var grain1 = KeyExtensionTestGrainFactory.GetGrain(baseKey, kx1);
            var grainId1 = await grain1.GetGrainId();

            Assert.AreEqual(((GrainReference)grain1).GrainId, grainId1, "Mismatched grain ID.");
        }
    }
}
