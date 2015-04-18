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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    [TestClass]
    public class GenericGrainTests : UnitTestSiloHost
    {

        public GenericGrainTests()
            : base(new UnitTestSiloOptions { StartPrimary = true, StartSecondary = false })
        {
        }

        public static I GetGrain<I>() where I : IGrainWithIntegerKey 
        {
            return GrainFactory.GetGrain<I>(GetRandomGrainId());
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

        /// Can instantiate multiple non-generic grain types that implement
        /// different specializations of the same generic interface
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task GenericGrainTests_NonGenericGrainForGenericInterfaceGetGrain()
        {

            var grainOfIntFloat = GetGrain<IGenericGrain<int, float>>();
            var grainOfIntString = GetGrain<IGenericGrain<int, string>>();

            var floatResult = await grainOfIntFloat.Ping(0);
            var stringResult = await grainOfIntString.Ping(1234);

            Assert.AreEqual(1.0f, floatResult);
            Assert.AreEqual("1234", stringResult); 
        }

        /// Can instantiate generic grain specializations
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task GenericGrainTests_SimpleGenericGrainGetGrain()
        {

            var grainOfFloat = GetGrain<ISimpleGenericGrain<float>>();
            var grainOfString = GetGrain<ISimpleGenericGrain<string>>();

            var floatResult = await grainOfFloat.Ping(12.34f);
            var stringResult = await grainOfString.Ping("1234");

            Assert.AreEqual(12.34f, floatResult);
            Assert.AreEqual("1234", stringResult);
        }

    }
}
