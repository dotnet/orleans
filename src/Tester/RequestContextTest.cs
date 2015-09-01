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
using NUnit.Framework;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;


namespace UnitTests.General
{
    [TestFixture]
    public class RequestContextTests : UnitTestSiloHost
    {
        public RequestContextTests()
            : base(new TestingSiloOptions { StartPrimary = true, StartSecondary = false })
        {
        }

        [TestFixtureTearDown]
        public void MyClassCleanup()
        {
            StopAllSilos();
        }

        [Test, Category("RequestContext"), Category("Functional")]
        public async Task RequestContextCallerToCalleeFlow()
        {
            var grain = GrainFactory.GetGrain<ISimplePersistentGrain>(random.Next());
            // Set context to send to the grain
            RequestContext.Set("GrainInfo", 10);
            // This grain method reads the context and returns it
            var infoFromGrain = await grain.GetRequestContext();
            Assert.IsNotNull(infoFromGrain);
            Assert.IsTrue((int)infoFromGrain == 10);
        }

        [Test, Category("RequestContext"), Category("Functional")]
        [ExpectedException(typeof(AssertionException))]
        public async Task RequestContextCalleeToCallerFlow()
        {
            var grain = GrainFactory.GetGrain<ISimplePersistentGrain>(random.Next());
            // This method in the grain does RequestContext.Set
            await grain.SetRequestContext(15);
            // Read the info set in the grain
            var infoFromGrain = RequestContext.Get("GrainInfo");
            Assert.IsNotNull(infoFromGrain);
            Assert.IsTrue((int)infoFromGrain == 15);
        }

    }
}
