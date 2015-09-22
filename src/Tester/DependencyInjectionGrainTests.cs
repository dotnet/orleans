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

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Framework.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests.General
{
    /// <summary>
    /// Summary description for SimpleGrain
    /// </summary>
    [DeploymentItem("OrleansStartupConfigurationForTesting.xml")]
    [TestClass]
    public class DependencyInjectionGrainTests : UnitTestSiloHost
    {
        private const string SimpleDIGrainNamePrefix = "UnitTests.Grains.SimpleDIG";

        public DependencyInjectionGrainTests()
            : base(new TestingSiloOptions
            {
                StartPrimary = true,
                StartSecondary = false,
                SiloConfigFile = new FileInfo("OrleansStartupConfigurationForTesting.xml")
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

    public class TestStartup
    {
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IInjectedService, InjectedService>();

            services.AddTransient<SimpleDIGrain>();

            return services.BuildServiceProvider();
        }
    }
}
