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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Autofac;
using Orleans.Providers;
using Orleans.Runtime;
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
        private const string SimpleDIGrainNamePrefix = "UnitTests.Grains.SimpleDIG";

        public DependencyResolverGrainTests()
            : base(new TestingSiloOptions
            {
                StartPrimary = true,
                StartSecondary = false,
                SiloConfigFile = new FileInfo("OrleansDependencyResolverConfigurationForTesting.xml")
            })
        {
        }

        public ISimpleDIGrain GetSimpleDIGrain()
        {
            return GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), SimpleDIGrainNamePrefix);
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
        public async Task SimpleDIGrainGetGrain()
        {
            ISimpleDIGrain grain = GetSimpleDIGrain();
            long ignored = await grain.GetTicksFromService();
        }
    }

    public class AutofacDependencyResolverProvider : DependencyResolverProviderBase
    {
        public override IDependencyResolver ConfigureResolver(System.Type[] systemTypesToRegister)
        {
            var builder = new ContainerBuilder();

            builder.RegisterTypes(systemTypesToRegister);

            //
            // We've to register the concrete type and the interface of the grain too:
            // - the test code is retrieving grains based on the interface.
            // - Orleans messaging is retrieving grains by their type.
            //

            // DI container grain registration helper method is used to make grain registration a no brainer.
            // One thing to note: at this point the type manager did not load all the assemblies,
            // so to support grain registration based on the loaded assemblies you have to make sure that the
            // assembly of the given grain types are loaded like in the sample below.
            //

            builder.RegisterGrains(typeof(SimpleDIGrain).Assembly)
                .AsSelf()
                .AsImplementedInterfaces();

            builder.RegisterType<InjectedService>()
                .AsImplementedInterfaces()
                .SingleInstance();

            var container = builder.Build();

            return new AutofacOrleansDependencyResolver(container);
        }
    }
}
