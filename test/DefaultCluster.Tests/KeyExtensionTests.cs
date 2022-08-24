using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    public class KeyExtensionTests : HostedTestClusterEnsureDefaultStarted
    {
        public KeyExtensionTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT"), TestCategory("PrimaryKeyExtension")]
        public async Task PrimaryKeyExtensionsShouldDifferentiateGrainsUsingTheSameBasePrimaryKey()
        {
            var baseKey = Guid.NewGuid();

            const string kx1 = "1";
            const string kx2 = "2";

            var grain1 = this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, kx1, null);
            var grainId1 = await grain1.GetGrainReference();
            var activationId1 = await grain1.GetActivationId();

            var grain2 = this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, kx2, null);
            var grainId2 = await grain2.GetGrainReference();
            var activationId2 = await grain2.GetActivationId();

            Assert.NotEqual(grainId1, grainId2); // Mismatched key extensions should differentiate an identical base primary key.
            Assert.NotEqual(activationId1, activationId2); // Mismatched key extensions should differentiate an identical base primary key.
        }

        [Fact, TestCategory("BVT"), TestCategory("PrimaryKeyExtension")]
        public async Task PrimaryKeyExtensionsShouldDifferentiateGrainsUsingDifferentBaseKeys()
        {
            var baseKey1 = Guid.NewGuid();
            var baseKey2 = Guid.NewGuid();

            const string kx = "1";

            var grain1 = this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey1, kx, null);
            var grainId1 = await grain1.GetGrainReference();
            var activationId1 = await grain1.GetActivationId();

            var grain2 = this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey2, kx, null);
            var grainId2 = await grain2.GetGrainReference();
            var activationId2 = await grain2.GetActivationId();

            Assert.NotEqual(grainId1, grainId2); // Mismatched base keys should differentiate between identical extended keys.
            Assert.NotEqual(activationId1, activationId2); // Mismatched base keys should differentiate between identical extended keys.
        }

        [Fact, TestCategory("BVT"), TestCategory("PrimaryKeyExtension")]
        public void EmptyKeyExtensionsAreDisallowed()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var baseKey = Guid.NewGuid();

                this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, "", null);
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("PrimaryKeyExtension")]
        public void WhiteSpaceKeyExtensionsAreDisallowed()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var baseKey = Guid.NewGuid();

                this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, " \t\n\r", null);
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("PrimaryKeyExtension")]
        public void NullKeyExtensionsAreDisallowed()
        {
            Assert.Throws<ArgumentNullException>(() =>
            {
                var baseKey = Guid.NewGuid();

                this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, null, null);
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("PrimaryKeyExtension")]
        public async Task PrimaryKeyExtensionsShouldPermitStringsLongerThan127BytesLong()
        {
            var baseKey = Guid.NewGuid();

            string kx1 = new string('\\', 300);

            var localGrainRef = this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, kx1, null);
            var remoteGrainRef = await localGrainRef.GetGrainReference();

            Assert.Equal(localGrainRef, remoteGrainRef); // Mismatched grain ID.
        }

        [Fact, TestCategory("BVT"), TestCategory("PrimaryKeyExtension")]
        public void GetPrimaryKeyStringOnGrainReference()
        {
            const string key = "foo";

            var grain = this.GrainFactory.GetGrain<IStringGrain>(key);
            var key2 = ((GrainReference) grain).GetPrimaryKeyString();

            Assert.Equal(key, key2); // Unexpected key was returned.
        }
    }
}
