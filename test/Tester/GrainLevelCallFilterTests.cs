using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Orleans.Hosting;
using Orleans.Serialization;

namespace UnitTests.General
{
    [TestCategory("BVT"), TestCategory("GrainLevelCallFilter")]
    public class GrainLevelCallFilterTests : OrleansTestingBase, IClassFixture<GrainLevelCallFilterTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            }
        }

        private readonly Fixture fixture;

        public GrainLevelCallFilterTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// Tests filters on just the grain level when there are no global grain call filters
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_Incoming_GrainLevel_Without_Global_Filter_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMethodInterceptionGrain>(0);
            var result = await grain.One();
            Assert.Equal("intercepted one with no args", result);

            result = await grain.Echo("stao erom tae");
            Assert.Equal("eat more oats", result);// Grain interceptors should receive the MethodInfo of the implementation, not the interface.

            result = await grain.NotIntercepted();
            Assert.Equal("not intercepted", result);

            result = await grain.SayHello();
            Assert.Equal("Hello", result);
        }
    }
}
