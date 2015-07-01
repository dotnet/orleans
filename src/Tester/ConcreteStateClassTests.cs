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
    public class StateClassTests : UnitTestSiloHost
    {
        private readonly Random rand = new Random();

        public StateClassTests()
            : base(new TestingSiloOptions {StartPrimary = true, StartSecondary = false})
        {
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StateClassTests_StateClass()
        {
            await StateClassTests_Test("UnitTests.Grains.SimplePersistentGrain");
        }

        private async Task StateClassTests_Test(string grainClass)
        {
            var x = rand.Next();
            var grain = GrainFactory.GetGrain<ISimplePersistentGrain>(x, grainClass);
            var originalVersion = await grain.GetVersion();
            await grain.SetA(x, true); // deactivate grain after setting A

            var newVersion = await grain.GetVersion(); // get a new version from the new activation
            Assert.AreNotEqual(originalVersion, newVersion);
            var a = await grain.GetA();
            Assert.AreEqual(x, a); // value of A survive deactivation and reactivation of the grain
        }
    }
}