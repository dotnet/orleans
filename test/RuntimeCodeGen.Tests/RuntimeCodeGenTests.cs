using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.ApplicationParts;
using Orleans.Hosting;
using Orleans.Logging;
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
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
            }
        }

        public class ClientBuilderConfigurator : IClientBuilderConfigurator {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                ILoggerFactory codeGenLoggerFactory = new LoggerFactory();
                codeGenLoggerFactory.AddProvider(new FileLoggerProvider("ClientCodeGeneration.log"));
                clientBuilder.ConfigureApplicationParts(
                    parts => parts.AddApplicationPart(typeof(IRuntimeCodeGenGrain).Assembly).WithCodeGeneration(codeGenLoggerFactory));
            }
        }

        public class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                ILoggerFactory codeGenLoggerFactory = new LoggerFactory();
                var siloName = hostBuilder.GetConfigurationValue("SiloName") ?? nameof(RuntimeCodeGenTests);
                codeGenLoggerFactory.AddProvider(new FileLoggerProvider($"{siloName}-CodeGeneration.log"));
                hostBuilder
                    .ConfigureApplicationParts(parts =>
                        parts.AddApplicationPart(typeof(IRuntimeCodeGenGrain).Assembly).WithCodeGeneration(codeGenLoggerFactory));
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

            var valueTaskResult = await grain.ValueTaskMethod(new RuntimeCodeGenPoco()).ConfigureAwait(false);
            Assert.IsType<RuntimeCodeGenPoco>(valueTaskResult);
            Assert.NotNull(valueTaskResult);
        }
    }
}