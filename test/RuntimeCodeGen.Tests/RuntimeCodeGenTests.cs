using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.ApplicationParts;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.TestingHost;
using RuntimeCodeGen.Interfaces;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests
{
    [TestCategory("CodeGen")]
    public class RuntimeCodeGenTests : OrleansTestingBase, IClassFixture<RuntimeCodeGenTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions();
                options.UseSiloBuilderFactory<SiloBuilder>();
                options.ClientBuilderFactory = cfg => ClientBuilder
                    .CreateDefault()
                    .UseConfiguration(cfg)
                    .ConfigureApplicationParts(
                        parts => parts.AddApplicationPart(typeof(IRuntimeCodeGenGrain).Assembly).WithCodeGeneration());

                return new TestCluster(options);
            }
        }

        public class SiloBuilder : ISiloBuilderFactory
        {
            public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
            {
                return SiloHostBuilder
                    .CreateDefault()
                    .UseConfiguration(clusterConfiguration)
                    .ConfigureSiloName(siloName)
                    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IRuntimeCodeGenGrain).Assembly).WithCodeGeneration());
            }
        }

        public RuntimeCodeGenTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT")]
        public void RuntimeCodeGen_AddsSupportClasses()
        {
            var partManager = new ApplicationPartManager();
            partManager.AddApplicationPart(typeof(IRuntimeCodeGenGrain).Assembly);
            partManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainInterfaceFeature>());
            partManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainClassFeature>());
            partManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<SerializerFeature>());

            var interfaceFeature = new GrainInterfaceFeature();
            partManager.PopulateFeature(interfaceFeature);
            Assert.DoesNotContain(interfaceFeature.Interfaces, i => i.InterfaceType == typeof(IRuntimeCodeGenGrain));

            var classFeature = new GrainClassFeature();
            partManager.PopulateFeature(classFeature);
            Assert.DoesNotContain(classFeature.Classes, c => c.ClassType == typeof(RuntimeCodeGenGrain));

            var serializerFeature = new SerializerFeature();
            partManager.PopulateFeature(serializerFeature);
            Assert.DoesNotContain(serializerFeature.SerializerTypes, s => s.Target == typeof(RuntimeCodeGenPoco));

            partManager.AddApplicationPart(typeof(IRuntimeCodeGenGrain).Assembly).WithCodeGeneration();
            interfaceFeature = new GrainInterfaceFeature();
            partManager.PopulateFeature(interfaceFeature);
            Assert.Contains(interfaceFeature.Interfaces, i => i.InterfaceType == typeof(IRuntimeCodeGenGrain));

            classFeature = new GrainClassFeature();
            partManager.PopulateFeature(classFeature);
            Assert.Contains(classFeature.Classes, c => c.ClassType == typeof(RuntimeCodeGenGrain));

            serializerFeature = new SerializerFeature();
            partManager.PopulateFeature(serializerFeature);
            Assert.Contains(serializerFeature.SerializerTypes, s => s.Target == typeof(RuntimeCodeGenPoco));
        }

        [Fact, TestCategory("Functional")]
        public async Task RuntimeCodeGen_BasicEndToEnd()
        {
            var grain = this.fixture.Client.GetGrain<IRuntimeCodeGenGrain>(Guid.NewGuid());
            var result = await grain.SomeMethod(new RuntimeCodeGenPoco());
            Assert.IsType<RuntimeCodeGenPoco>(result);
            Assert.NotNull(result);
        }
    }
}