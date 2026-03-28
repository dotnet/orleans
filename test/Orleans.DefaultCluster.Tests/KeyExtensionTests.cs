using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests for Orleans grain key extensions functionality.
    /// Key extensions allow grains to have compound keys by combining a primary key
    /// (GUID, long, or string) with a string extension. This enables scenarios like
    /// multi-tenancy where the same grain type can have different instances per tenant.
    /// </summary>
    public class KeyExtensionTests : HostedTestClusterEnsureDefaultStarted
    {
        public KeyExtensionTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Verifies that grains with the same base primary key but different key extensions
        /// are treated as distinct grain instances with separate activations.
        /// This is fundamental for multi-tenancy and partitioning scenarios.
        /// </summary>
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

        /// <summary>
        /// Verifies that grains with different base primary keys are distinct
        /// even when they share the same key extension value.
        /// Ensures proper isolation between different grain instances.
        /// </summary>
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

        /// <summary>
        /// Verifies that empty string key extensions are not allowed.
        /// This prevents accidental grain identity confusion and ensures
        /// meaningful key extensions when used.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("PrimaryKeyExtension")]
        public void EmptyKeyExtensionsAreDisallowed()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var baseKey = Guid.NewGuid();

                this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, "", null);
            });
        }

        /// <summary>
        /// Verifies that whitespace-only key extensions are not allowed.
        /// This ensures key extensions contain meaningful identifiers
        /// and prevents subtle bugs from invisible characters.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("PrimaryKeyExtension")]
        public void WhiteSpaceKeyExtensionsAreDisallowed()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var baseKey = Guid.NewGuid();

                this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, " \t\n\r", null);
            });
        }

        /// <summary>
        /// Verifies that null key extensions are not allowed.
        /// Enforces that key extensions must be explicitly provided
        /// when using the extended grain factory methods.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("PrimaryKeyExtension")]
        public void NullKeyExtensionsAreDisallowed()
        {
            Assert.Throws<ArgumentNullException>(() =>
            {
                var baseKey = Guid.NewGuid();

                this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, null, null);
            });
        }

        /// <summary>
        /// Verifies that key extensions can exceed 127 bytes in length.
        /// This ensures the system can handle long identifiers such as
        /// file paths, URLs, or other extended identifiers without artificial limits.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("PrimaryKeyExtension")]
        public async Task PrimaryKeyExtensionsShouldPermitStringsLongerThan127BytesLong()
        {
            var baseKey = Guid.NewGuid();

            string kx1 = new string('\\', 300);

            var localGrainRef = this.GrainFactory.GetGrain<IKeyExtensionTestGrain>(baseKey, kx1, null);
            var remoteGrainRef = await localGrainRef.GetGrainReference();

            Assert.Equal(localGrainRef, remoteGrainRef); // Mismatched grain ID.
        }

        /// <summary>
        /// Tests retrieving the primary key string from a grain reference.
        /// Verifies that string-keyed grains can have their keys extracted
        /// from grain references for diagnostic or routing purposes.
        /// </summary>
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
