using System;
using System.Threading.Tasks;
using Orleans;
using Tester.CodeGenTests;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    [TestCategory("CodeGen")]
    public class CodeGeneratorTests_AccessibilityChecks : HostedTestClusterEnsureDefaultStarted
    {
        public CodeGeneratorTests_AccessibilityChecks(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests that compile-time code generation supports classes marked as internal and that
        /// runtime code generation does not.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact, TestCategory("BVT")]
        public async Task CodeGenInterfaceAccessibilityCheckTest()
        {
            // Runtime codegen does not support internal interfaces.
            Assert.Throws<InvalidOperationException>(() => this.GrainFactory.GetGrain<IRuntimeInternalPingGrain>(9));

            // Compile-time codegen supports internal interfaces.
            var grain = this.GrainFactory.GetGrain<IInternalPingGrain>(0);
            await grain.Ping();
        }
    }

    internal interface IRuntimeInternalPingGrain : IGrainWithIntegerKey
    {
        Task Ping();
    }

    internal class InternalPingGrain : Grain, IInternalPingGrain, IRuntimeInternalPingGrain
    {
        public Task Ping() => Task.FromResult(0);
    }
}
