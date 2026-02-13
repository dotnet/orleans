using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests debugger helper functionality in Orleans.
    /// Orleans provides debugging utilities that allow developers to inspect grain instances
    /// during debugging sessions. This is useful for troubleshooting grain state and behavior
    /// in development environments. The debugger helper provides access to the actual grain
    /// instance behind the grain reference proxy.
    /// </summary>
    public class DebuggerHelperTests : HostedTestClusterEnsureDefaultStarted
    {
        public DebuggerHelperTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests the ability to get the actual grain instance for debugging purposes.
        /// Verifies that the OrleansDebuggerHelper can retrieve the concrete grain implementation
        /// from within the grain itself. This is useful for inspecting private state during debugging.
        /// Note: This functionality should only be used in development/debugging scenarios.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task DebuggerHelper_GetGrainInstance()
        {
            var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid()).Cast<IDebuggerHelperTestGrain>();
            await grain.OrleansDebuggerHelper_GetGrainInstance_Test();
        }
    }
}
