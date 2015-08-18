/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System.IO;
using System.Threading.Tasks;
using Autofac;
using Autofac.Builder;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Autofac;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.ReminderService;
using Orleans.Streams;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests.General
{
    /// <summary>
    /// Summary description for SimpleGrain
    /// </summary>
    [DeploymentItem("OrleansDependencyResolverConfigurationForTesting.xml")]
    [DeploymentItem("Orleans.Autofac.dll")]
    [TestClass]
    public class DependencyResolverGrainTests : UnitTestSiloHost
    {
        private const string SimpleGrainNamePrefix = "UnitTests.Grains.SimpleG";

        public DependencyResolverGrainTests()
            : base(new TestingSiloOptions
            {
                StartPrimary = true,
                StartSecondary = false,
                SiloConfigFile = new FileInfo("OrleansDependencyResolverConfigurationForTesting.xml")
            })
        {
        }

        public ISimpleGrain GetSimpleGrain()
        {
            return GrainFactory.GetGrain<ISimpleGrain>(GetRandomGrainId(), SimpleGrainNamePrefix);
        }

        private static int GetRandomGrainId()
        {
            return random.Next();
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DISimpleGrainGetGrain()
        {
            ISimpleGrain grain = GetSimpleGrain();
            int ignored = await grain.GetAxB();
        }
    }

    public class AutofacDepdendencyResolverProvider : IDependencyResolverProvider
    {
        public string Name
        {
            get { return "Autofac DI"; }
        }

        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            //
            // This method will not be called by the DependenceResolverProviderManager, when loading the provider.
            //

            throw new System.NotSupportedException();
        }

        public IDependencyResolver GetDependencyResolver(ClusterConfiguration config, NodeConfiguration nodeConfig, TraceLogger logger)
        {
            var builder = new ContainerBuilder();

            builder.RegisterType(typeof(GrainBasedMembershipTable));
            builder.RegisterType(typeof(GrainBasedReminderTable));
            builder.RegisterType(typeof(GrainBasedPubSubRuntime));

            //
            // We've to register the concrete type and the interface of the grain too:
            // - the test code is retrieving grains based on the interface.
            // - Orleans messaging is retrieving grains by their type.
            //
            // DI container grain registration helper functions can be written to make grain registration
            // a no brainer. One thing to note: at this point the type manager did not load all the assemblies,
            // so to support grain registration based on the loaded assemblies assembly loader should be invoked
            // earlier in Silo startup.
            //

            builder.RegisterType<SimpleGrain>();
            builder.RegisterType<SimpleGrain>().AsImplementedInterfaces<ISimpleGrain, ConcreteReflectionActivatorData>();

            var container = builder.Build();

            return new AutofacOrleansDependencyResolver(container);
        }
    }
}
