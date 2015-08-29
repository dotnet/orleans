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

using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Orleans.Runtime;

namespace UnitTests.General
{
    /// <summary>
    /// Summary description for SimpleGrain
    /// </summary>
    [TestClass]
    public class RequestContextTests : UnitTestSiloHost
    {
        private const string SimpleGrainNamePrefix = "UnitTests.Grains.SimpleRequestContextG";
        
        public RequestContextTests()
            : base(new TestingSiloOptions { StartPrimary = true, StartSecondary = false })
        {
        }

        public ISimpleRequestContextGrain GetSimpleRequestContextGrain()
        {
            return GrainFactory.GetGrain<ISimpleRequestContextGrain>(GetRandomGrainId(), SimpleGrainNamePrefix);
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

        [TestMethod, TestCategory("RequestContext"), TestCategory("Functional")]
        public async Task CallerToCalleeflow()
        {
            var grain = GetSimpleRequestContextGrain();
            // Set context to send to the grain
            RequestContext.Set("GrainInfo", 10L);
            // This grain method reads the context and returns it
            var infoFromGrain = await grain.ReadRequestContext();
            Assert.IsNotNull(infoFromGrain);
            Assert.IsTrue((long)infoFromGrain == 10);
        }

        [TestMethod, TestCategory("RequestContext"), TestCategory("Functional")]
        public async Task CalleeToCallerflow()
        {
            var grain = GetSimpleRequestContextGrain();
            // This method in the grain does RequestContext.Set
            await grain.GetRequestContext();
            // Read the info set in the grain
            var infoFromGrain = RequestContext.Get("GrainInfo");
            Assert.IsNotNull(infoFromGrain);
            Assert.IsTrue((long)infoFromGrain == 10);
        }

    }
}
