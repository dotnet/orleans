using Orleans;
using Orleans.ApplicationParts;
using Orleans.Providers;
using Xunit;

namespace UnitTests
{
    /// <summary>
    /// Tests for functionality related to <see cref="IApplicationPartManager"/>.
    /// </summary>
    [TestCategory("ApplicationPartManager"), TestCategory("BVT")]
    public class ApplicationPartManagerTests
    {
        /// <summary>
        /// Tests that <see cref="ApplicationPartManagerExtensions.AddFromApplicationBaseDirectory"/> correctly includes provider assemblies.
        /// </summary>
        [Fact]
        public void AddFromApplicationBaseDirectory_Includes_Providers()
        {
            var parts = new ApplicationPartManager().AddFromApplicationBaseDirectory();
            Assert.Contains(
                parts.Assemblies,
                asm => asm == typeof(MemoryAdapterFactory<>).Assembly);
        }
    }
}