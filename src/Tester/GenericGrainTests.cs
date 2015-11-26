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
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using System.Collections.Generic;
using TestGrainInterfaces;

namespace UnitTests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    [TestClass]
    public class GenericGrainTests : UnitTestSiloHost
    {
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(10);
        private static int grainId = 0;

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
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
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
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
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
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
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

        /// Can instantiate grains that implement generic interfaces with generic type parameters
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task GenericGrainTests_GenericInterfaceWithGenericParametersGetGrain()
        {

            var grain = GetGrain<ISimpleGenericGrain<List<float>>>();
            var list = new List<float>();
            list.Add(0.1f);
            await grain.Set(list);

            var result = await grain.Get();

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(0.1f, result[0]);
        }


        /// Multiple GetGrain requests with the same id return the same generic grain specialization
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
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
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
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
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
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
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
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
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
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
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
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

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task GenericGrainTests_UseGenericFactoryInsideGrain()
        {
            var grainId = GetRandomGrainId();

            var grainRef1 = GetGrain<ISimpleGenericGrain<string>>(grainId);
            await grainRef1.Set("JustString");
            await grainRef1.CompareGrainReferences(grainRef1);
        }


        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_SimpleGrain_GetGrain()
        {
            var grain = GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);
            await grain.GetA();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainControlFlow()
        {
            var a = random.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var grain = GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);

            await grain.SetA(a);

            await grain.SetB(b);

            Task<string> stringPromise = grain.GetAxB();
            Assert.AreEqual(expected, stringPromise.Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainDataFlow()
        {
            var a = random.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var grain = GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);

            var setAPromise = grain.SetA(a);
            var setBPromise = grain.SetB(b);
            var stringPromise = Task.WhenAll(setAPromise, setBPromise).ContinueWith((_) => grain.GetAxB()).Unwrap();

            var x = await stringPromise;
            Assert.AreEqual(expected, x, "Got expected result");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_SimpleGrain2_GetGrain()
        {
            var g1 = GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);
            var g2 = GrainFactory.GetGrain<ISimpleGenericGrainU<int>>(grainId++);
            var g3 = GrainFactory.GetGrain<ISimpleGenericGrain2<int, int>>(grainId++);
            await g1.GetA();
            await g2.GetA();
            await g3.GetA();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainControlFlow2_GetAB()
        {
            var a = random.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var g1 = GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);
            var g2 = GrainFactory.GetGrain<ISimpleGenericGrainU<int>>(grainId++);
            var g3 = GrainFactory.GetGrain<ISimpleGenericGrain2<int, int>>(grainId++);

            string r1 = await g1.GetAxB(a, b);
            string r2 = await g2.GetAxB(a, b);
            string r3 = await g3.GetAxB(a, b);
            Assert.AreEqual(expected, r1, "Grain 1");
            Assert.AreEqual(expected, r2, "Grain 2");
            Assert.AreEqual(expected, r3, "Grain 3");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainControlFlow3()
        {
            ISimpleGenericGrain2<int, float> g = GrainFactory.GetGrain<ISimpleGenericGrain2<int, float>>(grainId++);
            await g.SetA(3);
            await g.SetB(1.25f);
            Assert.AreEqual("3x1.25", await g.GetAxB());
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_SelfManagedGrainControlFlow()
        {
            IGenericSelfManagedGrain<int, float> g = GrainFactory.GetGrain<IGenericSelfManagedGrain<int, float>>(0);
            await g.SetA(3);
            await g.SetB(1.25f);
            Assert.AreEqual("3x1.25", await g.GetAxB());
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task GrainWithListFields()
        {
            string a = random.Next(100).ToString(CultureInfo.InvariantCulture);
            string b = random.Next(100).ToString(CultureInfo.InvariantCulture);

            var g1 = GrainFactory.GetGrain<IGrainWithListFields>(grainId++);

            var p1 = g1.AddItem(a);
            var p2 = g1.AddItem(b);
            await Task.WhenAll(p1, p2);

            var r1 = await g1.GetItems();

            Assert.IsTrue(
                (a == r1[0] && b == r1[1]) || (b == r1[0] && a == r1[1]), // Message ordering was not necessarily preserved.
                "Result: r[0]={0}, r[1]={1}", r1[0], r1[1]);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_GrainWithListFields()
        {
            int a = random.Next(100);
            int b = random.Next(100);


            var g1 = GrainFactory.GetGrain<IGenericGrainWithListFields<int>>(grainId++);

            var p1 = g1.AddItem(a);
            var p2 = g1.AddItem(b);
            await Task.WhenAll(p1, p2);

            var r1 = await g1.GetItems();

            Assert.IsTrue(
                (a == r1[0] && b == r1[1]) || (b == r1[0] && a == r1[1]), // Message ordering was not necessarily preserved.
                "Result: r[0]={0}, r[1]={1}", r1[0], r1[1]);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_GrainWithNoProperties_ControlFlow()
        {
            int a = random.Next(100);
            int b = random.Next(100);
            string expected = a + "x" + b;

            var g1 = GrainFactory.GetGrain<IGenericGrainWithNoProperties<int>>(grainId++);

            string r1 = await g1.GetAxB(a, b);
            Assert.AreEqual(expected, r1);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task GrainWithNoProperties_ControlFlow()
        {
            int a = random.Next(100);
            int b = random.Next(100);
            string expected = a + "x" + b;

            long grainId = GetRandomGrainId();
            var g1 = GrainFactory.GetGrain<IGrainWithNoProperties>(grainId);

            string r1 = await g1.GetAxB(a, b);
            Assert.AreEqual(expected, r1);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_ReaderWriterGrain1()
        {
            int a = random.Next(100);
            var g = GrainFactory.GetGrain<IGenericReaderWriterGrain1<int>>(grainId++);
            await g.SetValue(a);
            var res = await g.GetValue();
            Assert.AreEqual(a, res);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_ReaderWriterGrain2()
        {
            int a = random.Next(100);
            string b = "bbbbb";

            var g = GrainFactory.GetGrain<IGenericReaderWriterGrain2<int, string>>(grainId++);
            await g.SetValue1(a);
            await g.SetValue2(b);
            var r1 = await g.GetValue1();
            Assert.AreEqual(a, r1);
            var r2 = await g.GetValue2();
            Assert.AreEqual(b, r2);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_ReaderWriterGrain3()
        {
            int a = random.Next(100);
            string b = "bbbbb";
            double c = 3.145;

            var g = GrainFactory.GetGrain<IGenericReaderWriterGrain3<int, string, double>>(grainId++);
            await g.SetValue1(a);
            await g.SetValue2(b);
            await g.SetValue3(c);
            var r1 = await g.GetValue1();
            Assert.AreEqual(a, r1);
            var r2 = await g.GetValue2();
            Assert.AreEqual(b, r2);
            var r3 = await g.GetValue3();
            Assert.AreEqual(c, r3);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_Non_Primitive_Type_Argument()
        {
            IEchoHubGrain<Guid, string> g1 = GrainFactory.GetGrain<IEchoHubGrain<Guid, string>>(1);
            IEchoHubGrain<Guid, int> g2 = GrainFactory.GetGrain<IEchoHubGrain<Guid, int>>(1);
            IEchoHubGrain<Guid, byte[]> g3 = GrainFactory.GetGrain<IEchoHubGrain<Guid, byte[]>>(1);

            Assert.AreNotEqual((GrainReference) g1, (GrainReference) g2);
            Assert.AreNotEqual((GrainReference) g1, (GrainReference) g3);
            Assert.AreNotEqual((GrainReference) g2, (GrainReference) g3);

            await g1.Foo(Guid.Empty, "", 1);
            await g2.Foo(Guid.Empty, 0, 2);
            await g3.Foo(Guid.Empty, new byte[] { }, 3);

            Assert.AreEqual(1, await g1.GetX());
            Assert.AreEqual(2, await g2.GetX());
            Assert.AreEqual(3m, await g3.GetX());
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_1()
        {
            const string msg1 = "Hello from EchoGenericChainGrain-1";

            IEchoGenericChainGrain<string> g1 = GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g1.Echo(msg1);
            Assert.AreEqual(msg1, received, "Echo");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_2()
        {
            const string msg2 = "Hello from EchoGenericChainGrain-2";

            IEchoGenericChainGrain<string> g2 = GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g2.Echo2(msg2);
            Assert.AreEqual(msg2, received, "Echo");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_3()
        {
            const string msg3 = "Hello from EchoGenericChainGrain-3";

            IEchoGenericChainGrain<string> g3 = GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g3.Echo3(msg3);
            Assert.AreEqual(msg3, received, "Echo");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_4()
        {
            const string msg4 = "Hello from EchoGenericChainGrain-4";

            var g4 = GrainClient.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g4.Echo4(msg4);
            Assert.AreEqual(msg4, received, "Echo");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_5()
        {
            const string msg5 = "Hello from EchoGenericChainGrain-5";

            var g5 = GrainClient.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g5.Echo5(msg5);
            Assert.AreEqual(msg5, received, "Echo");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_6()
        {
            const string msg6 = "Hello from EchoGenericChainGrain-6";

            var g6 = GrainClient.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g6.Echo6(msg6);
            Assert.AreEqual(msg6, received, "Echo");
        }


        [TestMethod, TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_1Argument_GenericCallOnly()
        {
            var grain = GrainFactory.GetGrain<IGeneric1Argument<string>>(Guid.NewGuid(), "UnitTests.Grains.Generic1ArgumentGrain");
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.Ping(s1);
            Assert.AreEqual(s1, s2);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Generics")]
        [ExpectedException(typeof(OrleansException))]
        public async Task Generic_1Argument_NonGenericCallFirst()
        {
            var id = Guid.NewGuid();
            var nonGenericFacet = GrainFactory.GetGrain<INonGenericBase>(id, "UnitTests.Grains.Generic1ArgumentGrain");
            try
            {
                await nonGenericFacet.Ping();
            }
            catch (AggregateException exc)
            {
                throw exc.GetBaseException();
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Generics")]
        [ExpectedException(typeof(OrleansException))]
        public async Task Generic_1Argument_GenericCallFirst()
        {
            var id = Guid.NewGuid();
            var grain = GrainFactory.GetGrain<IGeneric1Argument<string>>(id, "UnitTests.Grains.Generic1ArgumentGrain");
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.Ping(s1);
            Assert.AreEqual(s1, s2);
            var nonGenericFacet = GrainFactory.GetGrain<INonGenericBase>(id, "UnitTests.Grains.Generic1ArgumentGrain");
            try
            {
                await nonGenericFacet.Ping();
            }
            catch (AggregateException exc)
            {
                throw exc.GetBaseException();
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Generics")]
        public async Task DifferentTypeArgsProduceIndependentActivations()
        {
            var grain1 = GrainFactory.GetGrain<IDbGrain<int>>(0);
            await grain1.SetValue(123);

            var grain2 = GrainFactory.GetGrain<IDbGrain<string>>(0);
            var v = await grain2.GetValue();
            Assert.IsNull(v);
        }
        
        [TestMethod, TestCategory("Functional"), TestCategory("Generics"), TestCategory("Echo")]
        public async Task Generic_PingSelf()
        {
            var id = Guid.NewGuid();
            var grain = GrainFactory.GetGrain<IGenericPingSelf<string>>(id);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.PingSelf(s1);
            Assert.AreEqual(s1, s2);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Generics"), TestCategory("Echo")]
        public async Task Generic_PingOther()
        {
            var id = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var grain = GrainFactory.GetGrain<IGenericPingSelf<string>>(id);
            var target = GrainFactory.GetGrain<IGenericPingSelf<string>>(targetId);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.PingOther(target, s1);
            Assert.AreEqual(s1, s2);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Generics"), TestCategory("Echo")]
        public async Task Generic_PingSelfThroughOther()
        {
            var id = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var grain = GrainFactory.GetGrain<IGenericPingSelf<string>>(id);
            var target = GrainFactory.GetGrain<IGenericPingSelf<string>>(targetId);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.PingSelfThroughOther(target, s1);
            Assert.AreEqual(s1, s2);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Generics"), TestCategory("ActivateDeactivate")]
        public async Task Generic_ScheduleDelayedPingAndDeactivate()
        {
            var id = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var grain = GrainFactory.GetGrain<IGenericPingSelf<string>>(id);
            var target = GrainFactory.GetGrain<IGenericPingSelf<string>>(targetId);
            var s1 = Guid.NewGuid().ToString();
            await grain.ScheduleDelayedPingToSelfAndDeactivate(target, s1, TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(6));
            var s2 = await grain.GetLastValue();
            Assert.AreEqual(s1, s2);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Generics"), TestCategory("Serialization")]
        public async Task Generic_CircularReferenceTest()
        {
            var grainId = Guid.NewGuid();
            var grain = GrainFactory.GetGrain<ICircularStateTestGrain>(primaryKey: grainId, keyExtension: grainId.ToString("N"));
            var c1 = await grain.GetState();
        }
    }
}
