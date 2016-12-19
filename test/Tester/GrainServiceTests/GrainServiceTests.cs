﻿using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester
{
    public class GrainServiceTests : TestClusterPerTest
    {
        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(1);
            options.ClusterConfiguration.UseStartupType<GrainServiceStartup>();
            options.ClusterConfiguration.Globals.RegisterGrainService("CustomGrainService", "Tester.CustomGrainServiceClient, Tester", "Tester.CustomGrainService, Tester");

            return new TestCluster(options);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainServices")]
        public async Task SimpleInvokeGrainService()
        {
            // We need to get the Silo to create the GrainService instances and register them as SystemTargets.

            IGrainServiceTestGrain grain = GrainFactory.GetGrain<IGrainServiceTestGrain>(0);
            string grainId = await grain.GetHelloWorldUsingCustomService();
            Assert.Equal("Hello World from Grain Service", grainId.ToString());
        }
    }


    public class GrainServiceStartup
    {
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ICustomGrainServiceClient, CustomGrainServiceClient>();
            return services.BuildServiceProvider();
        }
    }
}