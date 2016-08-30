using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace UnitTests
{
    public class AssemblyLoaderTests
    {
        const string ExpectedFileName = "OrleansProviders.dll";
        private readonly Logger logger = LogManager.GetLogger("AssemblyLoaderTests", LoggerType.Application);

        public AssemblyLoaderTests()
        {
            SerializationManager.InitializeForTesting();
        }

        [Fact, TestCategory("AssemblyLoader"), TestCategory("BVT"), TestCategory("Functional")]
        public void PathBasedAssemblyCatalogTest()
        {
            var catalog = NewCatalog()
                .WithAssembly("TestInternalGrainInterfaces");

            Assert.True(catalog.GetAssemblies().Count == 4);
        }

        [Fact, TestCategory("AssemblyLoader"), TestCategory("BVT"), TestCategory("Functional")]
        public void AssemblyLoaderTest()
        {
            var catalog = NewCatalog();
            var loader = AssemblyLoader.NewAssemblyLoader(catalog);
        }

        private AssemblyCatalog NewCatalog()
        {
            return new AssemblyCatalog()
                .WithAssembly("TestInternalGrains");
        }
    }
}
