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
using Orleans.TestingHost;
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
            : base(new TestingSiloOptions { StartPrimary = true, StartSecondary = false })
        {
        }

        public TGrainInterface GetGrain<TGrainInterface>(int i) where TGrainInterface : IGrainWithIntegerKey
        {
            return GrainFactory.GetGrain<TGrainInterface>(i);
        }

        public TGrainInterface GetGrain<TGrainInterface>() where TGrainInterface : IGrainWithIntegerKey 
        {
            return GrainFactory.GetGrain<TGrainInterface>(GetRandomGrainId());
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

        /// Can instantiate multiple concrete grain types that implement
        /// different specializations of the same generic interface
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task GenericGrainTests_ConcreteGrainWithGenericInterfaceGetGrain()
        {

            var grainOfIntFloat1 = GetGrain<IGenericGrain<int, float>>();
            var grainOfIntFloat2 = GetGrain<IGenericGrain<int, float>>();
            var grainOfFloatString = GetGrain<IGenericGrain<float, string>>();

            await grainOfIntFloat1.SetT(123);
            await grainOfIntFloat2.SetT(456);
            await grainOfFloatString.SetT(789.0f);

            var floatResult1 = await grainOfIntFloat1.MapT2U();
            var floatResult2 = await grainOfIntFloat2.MapT2U();
            var stringResult = await grainOfFloatString.MapT2U();

            Assert.AreEqual(123f, floatResult1);
            Assert.AreEqual(456f, floatResult2);
            Assert.AreEqual("789", stringResult); 
        }

        /// Multiple GetGrain requests with the same id return the same concrete grain 
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task GenericGrainTests_ConcreteGrainWithGenericInterfaceMultiplicity()
        {
            var grainId = GetRandomGrainId();

            var grainRef1 = GetGrain<IGenericGrain<int, float>>(grainId);
            await grainRef1.SetT(123);

            var grainRef2 = GetGrain<IGenericGrain<int, float>>(grainId);
            var floatResult = await grainRef2.MapT2U();

            Assert.AreEqual(123f, floatResult);
        }

        /// Can instantiate generic grain specializations
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task GenericGrainTests_SimpleGenericGrainGetGrain()
        {

            var grainOfFloat1 = GetGrain<ISimpleGenericGrain<float>>();
            var grainOfFloat2 = GetGrain<ISimpleGenericGrain<float>>();
            var grainOfString = GetGrain<ISimpleGenericGrain<string>>();

            await grainOfFloat1.Set(1.2f);
            await grainOfFloat2.Set(3.4f);
            await grainOfString.Set("5.6");

            // generic grain implementation does not change the set value:
            await grainOfFloat1.Transform();
            await grainOfFloat2.Transform();
            await grainOfString.Transform();

            var floatResult1 = await grainOfFloat1.Get();
            var floatResult2 = await grainOfFloat2.Get();
            var stringResult = await grainOfString.Get();

            Assert.AreEqual(1.2f, floatResult1);
            Assert.AreEqual(3.4f, floatResult2);
            Assert.AreEqual("5.6", stringResult);
        }

        /// Multiple GetGrain requests with the same id return the same generic grain specialization
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task GenericGrainTests_SimpleGenericGrainMultiplicity()
        {
            var grainId = GetRandomGrainId();

            var grainRef1 = GetGrain<ISimpleGenericGrain<float>>(grainId);
            await grainRef1.Set(1.2f);
            await grainRef1.Transform();  // NOP for generic grain class

            var grainRef2 = GetGrain<ISimpleGenericGrain<float>>(grainId);
            var floatResult = await grainRef2.Get();

            Assert.AreEqual(1.2f, floatResult);
        }

        /// If both a concrete implementation and a generic implementation of a 
        /// generic interface exist, prefer the concrete implementation.
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task GenericGrainTests_PreferConcreteGrainImplementationOfGenericInterface()
        {
            var grainOfDouble1 = GetGrain<ISimpleGenericGrain<double>>();
            var grainOfDouble2 = GetGrain<ISimpleGenericGrain<double>>();

            await grainOfDouble1.Set(1.0);
            await grainOfDouble2.Set(2.0);

            // concrete implementation (SpecializedSimpleGenericGrain) doubles the set value:
            await grainOfDouble1.Transform();
            await grainOfDouble2.Transform();

            var result1 = await grainOfDouble1.Get();
            var result2 = await grainOfDouble2.Get();

            Assert.AreEqual(2.0, result1);
            Assert.AreEqual(4.0, result2);
        }

        /// Multiple GetGrain requests with the same id return the same concrete grain implementation
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task GenericGrainTests_PreferConcreteGrainImplementationOfGenericInterfaceMultiplicity()
        {
            var grainId = GetRandomGrainId();

            var grainRef1 = GetGrain<ISimpleGenericGrain<double>>(grainId);
            await grainRef1.Set(1.0);
            await grainRef1.Transform();  // SpecializedSimpleGenericGrain doubles the value for generic grain class

            // a second reference with the same id points to the same grain:
            var grainRef2 = GetGrain<ISimpleGenericGrain<double>>(grainId);
            await grainRef2.Transform();
            var floatResult = await grainRef2.Get();

            Assert.AreEqual(4.0f, floatResult);
        }

        /// Can instantiate concrete grains that implement multiple generic interfaces
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task GenericGrainTests_ConcreteGrainWithMultipleGenericInterfacesGetGrain()
        {
            var grain1 = GetGrain<ISimpleGenericGrain<int>>();
            var grain2 = GetGrain<ISimpleGenericGrain<int>>();

            await grain1.Set(1);
            await grain2.Set(2);

            // ConcreteGrainWith2GenericInterfaces multiplies the set value by 10:
            await grain1.Transform();
            await grain2.Transform();

            var result1 = await grain1.Get();
            var result2 = await grain2.Get();

            Assert.AreEqual(10, result1);
            Assert.AreEqual(20, result2);
        }

        /// Multiple GetGrain requests with the same id and interface return the same concrete grain implementation
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task GenericGrainTests_ConcreteGrainWithMultipleGenericInterfacesMultiplicity1()
        {
            var grainId = GetRandomGrainId();

            var grainRef1 = GetGrain<ISimpleGenericGrain<int>>(grainId);
            await grainRef1.Set(1);

            // ConcreteGrainWith2GenericInterfaces multiplies the set value by 10:
            await grainRef1.Transform();  

            //A second reference to the interface will point to the same grain
            var grainRef2 = GetGrain<ISimpleGenericGrain<int>>(grainId);
            await grainRef2.Transform();
            var floatResult = await grainRef2.Get();

            Assert.AreEqual(100, floatResult);
        }

        /// Multiple GetGrain requests with the same id and different interfaces return the same concrete grain implementation
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task GenericGrainTests_ConcreteGrainWithMultipleGenericInterfacesMultiplicity2()
        {
            var grainId = GetRandomGrainId();

            var grainRef1 = GetGrain<ISimpleGenericGrain<int>>(grainId);
            await grainRef1.Set(1);
            await grainRef1.Transform();  // ConcreteGrainWith2GenericInterfaces multiplies the set value by 10:

            // A second reference to a different interface implemented by ConcreteGrainWith2GenericInterfaces 
            // will reference the same grain:
            var grainRef2 = GetGrain<IGenericGrain<int, string>>(grainId);
            // ConcreteGrainWith2GenericInterfaces returns a string representation of the current value multiplied by 10:
            var floatResult = await grainRef2.MapT2U(); 

            Assert.AreEqual("100", floatResult);
        }

        [TestMethod, TestCategory("Failures")]
        public async Task GenericGrainTests_UseGenericFactoryInsideGrain()
        {
            var grainId = GetRandomGrainId();

            var grainRef1 = GetGrain<ISimpleGenericGrain<string>>(grainId);
            await grainRef1.Set("JustString");
            await grainRef1.CompareGrainReferences();
        }
    }
}
