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

namespace UnitTests.General
{
    /// <summary>
    /// Summary description for SimpleGrain
    /// </summary>
    [TestClass]
    public class SimpleGrainTests : UnitTestSiloHost
    {
        private const string SimpleGrainNamePrefix = "UnitTests.Grains.SimpleG";
        
        public SimpleGrainTests()
            : base(new TestingSiloOptions { StartPrimary = true, StartSecondary = false })
        {
        }

        public static ISimpleGrain GetSimpleGrain()
        {
            return SimpleGrainFactory.GetGrain(GetRandomGrainId(), SimpleGrainNamePrefix);
        }

        public static ISimpleGrain GetSimpleGrain(long grainId)
        {
            return SimpleGrainFactory.GetGrain(grainId, SimpleGrainNamePrefix);
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

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task SimpleGrainGetGrain()
        {
            ISimpleGrain grain = GetSimpleGrain();
            int ignored = await grain.GetAxB();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public void SimpleGrainControlFlow()
        {
            ISimpleGrain grain = GetSimpleGrain();
            
            Task setPromise = grain.SetA(2);
            setPromise.Wait();

            setPromise = grain.SetB(3);
            setPromise.Wait();

            Task<int> intPromise = grain.GetAxB();
            Assert.AreEqual(6, intPromise.Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task SimpleGrainDataFlow()
        {
            ISimpleGrain grain = GetSimpleGrain();

            Task setAPromise = grain.SetA(3);
            Task setBPromise = grain.SetB(4);
            await Task.WhenAll(setAPromise, setBPromise);
            var x = await grain.GetAxB();

            Assert.AreEqual(12, x);
        }
    }
}
