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

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
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

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
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

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public void EmptyKeyExtensionsAreDisallowed()
        {
            Xunit.Assert.Throws(typeof(ArgumentException), () =>
            {
                var baseKey = Guid.NewGuid();

                this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, "", null);
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public void WhiteSpaceKeyExtensionsAreDisallowed()
        {
            Xunit.Assert.Throws(typeof(ArgumentException), () =>
            {
                var baseKey = Guid.NewGuid();

                this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, " \t\n\r", null);
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public void NullKeyExtensionsAreDisallowed()
        {
            Xunit.Assert.Throws(typeof(ArgumentNullException), () =>
            {
                var baseKey = Guid.NewGuid();

                this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, null, null);
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public async Task PrimaryKeyExtensionsShouldPermitStringsLongerThan127BytesLong()
        {
            var baseKey = Guid.NewGuid();

            string kx1 = new string('\\', 300);

            var localGrainRef = this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, kx1, null);
            var remoteGrainRef = await localGrainRef.GetGrainReference();

            Assert.Equal(localGrainRef, remoteGrainRef); // Mismatched grain ID.
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public void GetPrimaryKeyStringOnGrainReference()
        {
            const string key = "foo";

            var grain = this.GrainFactory.GetGrain<IStringGrain>(key);
            var key2 = ((GrainReference) grain).GetPrimaryKeyString();

            Assert.Equal(key, key2); // Unexpected key was returned.
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public void KeysAllowPlusSymbols()
        {
            const string key = "foo+bar+zaz";

            {
                // Verify that grains with string keys can include + symbols in their key.
                var grain = this.GrainFactory.GetGrain<IStringGrain>(key);
                var grainRef = (GrainReference) grain;
                var key2 = grainRef.GetPrimaryKeyString();
                Assert.Equal(key, key2);

                var grainRef2 = GrainReference.FromKeyString(
                    grainRef.ToKeyString(),
                    this.Client.ServiceProvider.GetRequiredService<IRuntimeClient>());
                Assert.True(grainRef.Equals(grainRef2));
            }

            {
                // Verify that grains with compound keys can include + symbols in their key extension.
                var primaryKey = Guid.NewGuid();
                var grain = this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(primaryKey, keyExtension: key);
                string keyExt;
                var grainRef = (GrainReference) grain;
                var actualPrimaryKey = grainRef.GetPrimaryKey(out keyExt);
                Assert.Equal(primaryKey, actualPrimaryKey);
                Assert.Equal(key, keyExt);

                var grainRef2 = GrainReference.FromKeyString(
                    grainRef.ToKeyString(),
                    this.Client.ServiceProvider.GetRequiredService<IRuntimeClient>());
                Assert.True(grainRef.Equals(grainRef2));
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("PrimaryKeyExtension")]
        public void GetPrimaryKeyStringOnWrongGrainReference()
        {
            var grain = this.GrainFactory.GetGrain<ISimpleGrain>(0);
            var key = ((GrainReference)grain).GetPrimaryKeyString();
            Assert.Null(key);
        }
    }
}
