using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Internal;
using Orleans.Runtime;
using TestExtensions;
using TestGrainInterfaces;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    public class GenericGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        private static int grainId = 0;

        public GenericGrainTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        public TGrainInterface GetGrain<TGrainInterface>(long i) where TGrainInterface : IGrainWithIntegerKey
        {
            return  this.GrainFactory.GetGrain<TGrainInterface>(i);
        }

        public TGrainInterface GetGrain<TGrainInterface>() where TGrainInterface : IGrainWithIntegerKey
        {
            return  this.GrainFactory.GetGrain<TGrainInterface>(GetRandomGrainId());
        }

        /// Can instantiate multiple concrete grain types that implement
        /// different specializations of the same generic interface
        [Fact, TestCategory("BVT"), TestCategory("Generics")]
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

            Assert.Equal(123f, floatResult1);
            Assert.Equal(456f, floatResult2);
            Assert.Equal("789", stringResult);
        }

        /// Multiple GetGrain requests with the same id return the same concrete grain 
        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task GenericGrainTests_ConcreteGrainWithGenericInterfaceMultiplicity()
        {
            var grainId = GetRandomGrainId();

            var grainRef1 = GetGrain<IGenericGrain<int, float>>(grainId);
            await grainRef1.SetT(123);

            var grainRef2 = GetGrain<IGenericGrain<int, float>>(grainId);
            var floatResult = await grainRef2.MapT2U();

            Assert.Equal(123f, floatResult);
        }

        /// Can instantiate generic grain specializations
        [Theory, TestCategory("BVT"), TestCategory("Generics")]
        [InlineData(1.2f)]
        [InlineData(3.4f)]
        [InlineData("5.6")]
        public async Task GenericGrainTests_SimpleGenericGrainGetGrain<T>(T setValue)
        {

            var grain = GetGrain<ISimpleGenericGrain<T>>();

            await grain.Set(setValue);

            // generic grain implementation does not change the set value:
            await grain.Transform();
            
            T result = await grain.Get();
            
            Assert.Equal(setValue, result);            
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task GenericGrainTests_SimpleGenericGrainGetGrain_ArrayTypeParameter()
        {
            var grain = GetGrain<ISimpleGenericGrain<int[]>>();

            var expected = new[] { 1, 2, 3 };
            await grain.Set(expected);

            // generic grain implementation does not change the set value:
            await grain.Transform();
            
            var result = await grain.Get();
            
            Assert.Equal(expected, result);            
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task GenericGrainTests_GenericGrainInheritingArray()
        {
            var grain = GetGrain<IGenericArrayRegisterGrain<int>>();

            var expected = new[] { 1, 2, 3 };
            await grain.Set(expected);

            var result = await grain.Get();
            
            Assert.Equal(expected, result);            
        }

        /// Can instantiate grains that implement generic interfaces with generic type parameters
        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task GenericGrainTests_GenericInterfaceWithGenericParametersGetGrain()
        {

            var grain = GetGrain<ISimpleGenericGrain<List<float>>>();
            var list = new List<float>();
            list.Add(0.1f);
            await grain.Set(list);

            var result = await grain.Get();

            Assert.Single(result);
            Assert.Equal(0.1f, result[0]);
        }


        /// Multiple GetGrain requests with the same id return the same generic grain specialization
        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task GenericGrainTests_SimpleGenericGrainMultiplicity()
        {
            var grainId = GetRandomGrainId();

            var grainRef1 = GetGrain<ISimpleGenericGrain<float>>(grainId);
            await grainRef1.Set(1.2f);
            await grainRef1.Transform();  // NOP for generic grain class

            var grainRef2 = GetGrain<ISimpleGenericGrain<float>>(grainId);
            var floatResult = await grainRef2.Get();

            Assert.Equal(1.2f, floatResult);
        }

        /// If both a concrete implementation and a generic implementation of a 
        /// generic interface exist, prefer the concrete implementation.
        [Fact, TestCategory("BVT"), TestCategory("Generics")]
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

            Assert.Equal(2.0, result1);
            Assert.Equal(4.0, result2);
        }

        /// Multiple GetGrain requests with the same id return the same concrete grain implementation
        [Fact, TestCategory("BVT"), TestCategory("Generics")]
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

            Assert.Equal(4.0f, floatResult);
        }

        /// Can instantiate concrete grains that implement multiple generic interfaces
        [Fact, TestCategory("BVT"), TestCategory("Generics")]
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

            Assert.Equal(10, result1);
            Assert.Equal(20, result2);
        }

        /// Multiple GetGrain requests with the same id and interface return the same concrete grain implementation
        [Fact, TestCategory("BVT"), TestCategory("Generics")]
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

            Assert.Equal(100, floatResult);
        }

        /// Multiple GetGrain requests with the same id and different interfaces return the same concrete grain implementation
        [Fact, TestCategory("BVT"), TestCategory("Generics")]
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

            Assert.Equal("100", floatResult);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task GenericGrainTests_UseGenericFactoryInsideGrain()
        {
            var grainId = GetRandomGrainId();

            var grainRef1 = GetGrain<ISimpleGenericGrain<string>>(grainId);
            await grainRef1.Set("JustString");
            await grainRef1.CompareGrainReferences(grainRef1);
        }


        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_SimpleGrain_GetGrain()
        {
            var grain =  this.GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);
            await grain.GetA();
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainControlFlow()
        {
            var a = Random.Shared.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var grain =  this.GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);

            await grain.SetA(a);

            await grain.SetB(b);

            Task<string> stringPromise = grain.GetAxB();
            Assert.Equal(expected, stringPromise.Result);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public void Generic_SimpleGrainControlFlow_Blocking()
        {
            var a = Random.Shared.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var grain =  this.GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);

            // explicitly use .Wait() and .Result to make sure the client does not deadlock in these cases.
            grain.SetA(a).Wait();

            grain.SetB(b).Wait();

            Task<string> stringPromise = grain.GetAxB();
            Assert.Equal(expected, stringPromise.Result);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainDataFlow()
        {
            var a = Random.Shared.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var grain =  this.GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);

            var setAPromise = grain.SetA(a);
            var setBPromise = grain.SetB(b);
            var stringPromise = Task.WhenAll(setAPromise, setBPromise).ContinueWith((_) => grain.GetAxB()).Unwrap();

            var x = await stringPromise;
            Assert.Equal(expected, x);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_SimpleGrain2_GetGrain()
        {
            var g1 =  this.GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);
            var g2 =  this.GrainFactory.GetGrain<ISimpleGenericGrainU<int>>(grainId++);
            var g3 =  this.GrainFactory.GetGrain<ISimpleGenericGrain2<int, int>>(grainId++);
            await g1.GetA();
            await g2.GetA();
            await g3.GetA();
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainGenericParameterWithMultipleArguments_GetGrain()
        {
            var g1 =  this.GrainFactory.GetGrain<ISimpleGenericGrain1<Dictionary<int, int>>>(GetRandomGrainId());
            await g1.GetA();
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainControlFlow2_GetAB()
        {
            var a = Random.Shared.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var g1 =  this.GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);
            var g2 =  this.GrainFactory.GetGrain<ISimpleGenericGrainU<int>>(grainId++);
            var g3 =  this.GrainFactory.GetGrain<ISimpleGenericGrain2<int, int>>(grainId++);

            string r1 = await g1.GetAxB(a, b);
            string r2 = await g2.GetAxB(a, b);
            string r3 = await g3.GetAxB(a, b);
            Assert.Equal(expected, r1);
            Assert.Equal(expected, r2);
            Assert.Equal(expected, r3);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainControlFlow3()
        {
            ISimpleGenericGrain2<int, float> g =  this.GrainFactory.GetGrain<ISimpleGenericGrain2<int, float>>(grainId++);
            await g.SetA(3);
            await g.SetB(1.25f);
            Assert.Equal("3x1.25", await g.GetAxB());
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_BasicGrainControlFlow()
        {
            IBasicGenericGrain<int, float> g =  this.GrainFactory.GetGrain<IBasicGenericGrain<int, float>>(0);
            await g.SetA(3);
            await g.SetB(1.25f);
            Assert.Equal("3x1.25", await g.GetAxB());
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task GrainWithListFields()
        {
            string a = Random.Shared.Next(100).ToString(CultureInfo.InvariantCulture);
            string b = Random.Shared.Next(100).ToString(CultureInfo.InvariantCulture);

            var g1 =  this.GrainFactory.GetGrain<IGrainWithListFields>(grainId++);

            var p1 = g1.AddItem(a);
            var p2 = g1.AddItem(b);
            await Task.WhenAll(p1, p2);

            var r1 = await g1.GetItems();

            Assert.True(
                (a == r1[0] && b == r1[1]) || (b == r1[0] && a == r1[1]), // Message ordering was not necessarily preserved.
                string.Format("Result: r[0]={0}, r[1]={1}", r1[0], r1[1]));
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_GrainWithListFields()
        {
            int a = Random.Shared.Next(100);
            int b = Random.Shared.Next(100);


            var g1 =  this.GrainFactory.GetGrain<IGenericGrainWithListFields<int>>(grainId++);

            var p1 = g1.AddItem(a);
            var p2 = g1.AddItem(b);
            await Task.WhenAll(p1, p2);

            var r1 = await g1.GetItems();

            Assert.True(
                (a == r1[0] && b == r1[1]) || (b == r1[0] && a == r1[1]), // Message ordering was not necessarily preserved.
                string.Format("Result: r[0]={0}, r[1]={1}", r1[0], r1[1]));
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_GrainWithNoProperties_ControlFlow()
        {
            int a = Random.Shared.Next(100);
            int b = Random.Shared.Next(100);
            string expected = a + "x" + b;

            var g1 =  this.GrainFactory.GetGrain<IGenericGrainWithNoProperties<int>>(grainId++);

            string r1 = await g1.GetAxB(a, b);
            Assert.Equal(expected, r1);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task GrainWithNoProperties_ControlFlow()
        {
            int a = Random.Shared.Next(100);
            int b = Random.Shared.Next(100);
            string expected = a + "x" + b;

            long grainId = GetRandomGrainId();
            var g1 =  this.GrainFactory.GetGrain<IGrainWithNoProperties>(grainId);

            string r1 = await g1.GetAxB(a, b);
            Assert.Equal(expected, r1);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_ReaderWriterGrain1()
        {
            int a = Random.Shared.Next(100);
            var g =  this.GrainFactory.GetGrain<IGenericReaderWriterGrain1<int>>(grainId++);
            await g.SetValue(a);
            var res = await g.GetValue();
            Assert.Equal(a, res);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_ReaderWriterGrain2()
        {
            int a = Random.Shared.Next(100);
            string b = "bbbbb";

            var g =  this.GrainFactory.GetGrain<IGenericReaderWriterGrain2<int, string>>(grainId++);
            await g.SetValue1(a);
            await g.SetValue2(b);
            var r1 = await g.GetValue1();
            Assert.Equal(a, r1);
            var r2 = await g.GetValue2();
            Assert.Equal(b, r2);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_ReaderWriterGrain3()
        {
            int a = Random.Shared.Next(100);
            string b = "bbbbb";
            double c = 3.145;

            var g =  this.GrainFactory.GetGrain<IGenericReaderWriterGrain3<int, string, double>>(grainId++);
            await g.SetValue1(a);
            await g.SetValue2(b);
            await g.SetValue3(c);
            var r1 = await g.GetValue1();
            Assert.Equal(a, r1);
            var r2 = await g.GetValue2();
            Assert.Equal(b, r2);
            var r3 = await g.GetValue3();
            Assert.Equal(c, r3);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_Non_Primitive_Type_Argument()
        {
            IEchoHubGrain<Guid, string> g1 =  this.GrainFactory.GetGrain<IEchoHubGrain<Guid, string>>(1);
            IEchoHubGrain<Guid, int> g2 =  this.GrainFactory.GetGrain<IEchoHubGrain<Guid, int>>(1);
            IEchoHubGrain<Guid, byte[]> g3 =  this.GrainFactory.GetGrain<IEchoHubGrain<Guid, byte[]>>(1);

            Assert.NotEqual((GrainReference)g1, (GrainReference)g2);
            Assert.NotEqual((GrainReference)g1, (GrainReference)g3);
            Assert.NotEqual((GrainReference)g2, (GrainReference)g3);

            await g1.Foo(Guid.Empty, "", 1);
            await g2.Foo(Guid.Empty, 0, 2);
            await g3.Foo(Guid.Empty, new byte[] { }, 3);

            Assert.Equal(1, await g1.GetX());
            Assert.Equal(2, await g2.GetX());
            Assert.Equal(3m, await g3.GetX());
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_1()
        {
            const string msg1 = "Hello from EchoGenericChainGrain-1";

            IEchoGenericChainGrain<string> g1 =  this.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g1.Echo(msg1);
            Assert.Equal(msg1, received);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_2()
        {
            const string msg2 = "Hello from EchoGenericChainGrain-2";

            IEchoGenericChainGrain<string> g2 =  this.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g2.Echo2(msg2);
            Assert.Equal(msg2, received);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_3()
        {
            const string msg3 = "Hello from EchoGenericChainGrain-3";

            IEchoGenericChainGrain<string> g3 =  this.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g3.Echo3(msg3);
            Assert.Equal(msg3, received);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_4()
        {
            const string msg4 = "Hello from EchoGenericChainGrain-4";

            var g4 = this.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g4.Echo4(msg4);
            Assert.Equal(msg4, received);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_5()
        {
            const string msg5 = "Hello from EchoGenericChainGrain-5";

            var g5 = this.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g5.Echo5(msg5);
            Assert.Equal(msg5, received);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_6()
        {
            const string msg6 = "Hello from EchoGenericChainGrain-6";

            var g6 = this.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g6.Echo6(msg6);
            Assert.Equal(msg6, received);
        }


        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_1Argument_GenericCallOnly()
        {
            var grain =  this.GrainFactory.GetGrain<IGeneric1Argument<string>>(Guid.NewGuid(), "UnitTests.Grains.Generic1ArgumentGrain");
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.Ping(s1);
            Assert.Equal(s1, s2);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_1Argument_NonGenericCallFirst()
        {

            var id = Guid.NewGuid();
            var nonGenericFacet =  this.GrainFactory.GetGrain<INonGenericBase>(id, "UnitTests.Grains.Generic1ArgumentGrain");
            await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                try
                {
                    await nonGenericFacet.Ping();
                }
                catch (AggregateException exc)
                {
                    throw exc.GetBaseException();
                }
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_1Argument_GenericCallFirst()
        {
            var id = Guid.NewGuid();
            var grain =  this.GrainFactory.GetGrain<IGeneric1Argument<string>>(id, "UnitTests.Grains.Generic1ArgumentGrain");
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.Ping(s1);
            Assert.Equal(s1, s2);
            var nonGenericFacet =  this.GrainFactory.GetGrain<INonGenericBase>(id, "UnitTests.Grains.Generic1ArgumentGrain");
            await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                try
                {
                    await nonGenericFacet.Ping();
                }
                catch (AggregateException exc)
                {
                    throw exc.GetBaseException();
                }
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task DifferentTypeArgsProduceIndependentActivations()
        {
            var grain1 =  this.GrainFactory.GetGrain<IDbGrain<int>>(0);
            await grain1.SetValue(123);

            var grain2 =  this.GrainFactory.GetGrain<IDbGrain<string>>(0);
            var v = await grain2.GetValue();
            Assert.Null(v);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("Echo")]
        public async Task Generic_PingSelf()
        {
            var id = Guid.NewGuid();
            var grain =  this.GrainFactory.GetGrain<IGenericPingSelf<string>>(id);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.PingSelf(s1);
            Assert.Equal(s1, s2);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("Echo")]
        public async Task Generic_PingOther()
        {
            var id = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var grain =  this.GrainFactory.GetGrain<IGenericPingSelf<string>>(id);
            var target =  this.GrainFactory.GetGrain<IGenericPingSelf<string>>(targetId);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.PingOther(target, s1);
            Assert.Equal(s1, s2);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("Echo")]
        public async Task Generic_PingSelfThroughOther()
        {
            var id = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var grain =  this.GrainFactory.GetGrain<IGenericPingSelf<string>>(id);
            var target =  this.GrainFactory.GetGrain<IGenericPingSelf<string>>(targetId);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.PingSelfThroughOther(target, s1);
            Assert.Equal(s1, s2);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("ActivateDeactivate")]
        public async Task Generic_ScheduleDelayedPingAndDeactivate()
        {
            var id = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var grain =  this.GrainFactory.GetGrain<IGenericPingSelf<string>>(id);
            var target =  this.GrainFactory.GetGrain<IGenericPingSelf<string>>(targetId);
            var s1 = Guid.NewGuid().ToString();
            await grain.ScheduleDelayedPingToSelfAndDeactivate(target, s1, TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(6));
            var s2 = await grain.GetLastValue();
            Assert.Equal(s1, s2);
        }

        [Fact, TestCategory("BVT"), TestCategory("Generics"), TestCategory("Serialization")]
        public async Task SerializationTests_Generic_CircularReferenceTest()
        {
            var grainId = Guid.NewGuid();
            var grain =  this.GrainFactory.GetGrain<ICircularStateTestGrain>(primaryKey: grainId, keyExtension: grainId.ToString("N"));
            _ = await grain.GetState();
        }
                
        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task Generic_GrainWithTypeConstraints()
        {
            var grainId = Guid.NewGuid().ToString();
            var grain =  this.GrainFactory.GetGrain<IGenericGrainWithConstraints<List<int>, int, string>>(grainId);
            var result = await grain.GetCount();
            Assert.Equal(0, result);
            await grain.Add(42);
            result = await grain.GetCount();
            Assert.Equal(1, result);

            var unmanagedGrain = this.GrainFactory.GetGrain<IUnmanagedArgGrain<DateTime>>(Guid.NewGuid());
            {
                var echoInput = DateTime.UtcNow;
                var echoOutput = await unmanagedGrain.Echo(echoInput);
                Assert.Equal(echoInput, echoOutput);
                echoOutput = await unmanagedGrain.EchoValue(echoInput);
                Assert.Equal(echoInput, echoOutput);
            }
            {
                var echoInput = "hello";
                var echoOutput = await unmanagedGrain.EchoNonNullable(echoInput);
                Assert.Equal(echoInput, echoOutput);
                echoOutput = await unmanagedGrain.EchoReference(echoInput);
                Assert.Equal(echoInput, echoOutput);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Persistence")]
        public async Task Generic_GrainWithValueTypeState()
        {
            Guid id = Guid.NewGuid();
            var grain = this.GrainFactory.GetGrain<IValueTypeTestGrain>(id);

            var initial = await grain.GetStateData();
            Assert.Equal(new ValueTypeTestData(0), initial);

            var expectedValue = new ValueTypeTestData(42);

            await grain.SetStateData(expectedValue);

            Assert.Equal(expectedValue, await grain.GetStateData());
        }

        [Fact(Skip = "https://github.com/dotnet/orleans/issues/1655 Casting from non-generic to generic interface fails with an obscure error message"), TestCategory("Functional"), TestCategory("Cast"), TestCategory("Generics")]
        public async Task Generic_CastToGenericInterfaceAfterActivation() 
        {
            var grain =  this.GrainFactory.GetGrain<INonGenericCastableGrain>(Guid.NewGuid());
            await grain.DoSomething(); //activates original grain type here

            var castRef = grain.AsReference<ISomeGenericGrain<string>>();

            var result = await castRef.Hello();

            Assert.Equal("Hello!", result);
        }

        [Fact(Skip= "https://github.com/dotnet/orleans/issues/1655 Casting from non-generic to generic interface fails with an obscure error message"), TestCategory("Functional"), TestCategory("Cast"), TestCategory("Generics")]
        public async Task Generic_CastToDifferentlyConcretizedGenericInterfaceBeforeActivation() {
            var grain =  this.GrainFactory.GetGrain<INonGenericCastableGrain>(Guid.NewGuid());

            var castRef = grain.AsReference<IIndependentlyConcretizedGenericGrain<string>>();

            var result = await castRef.Hello();

            Assert.Equal("Hello!", result);
        }
        
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Generic_CastToDifferentlyConcretizedInterfaceBeforeActivation() {
            var grain =  this.GrainFactory.GetGrain<INonGenericCastableGrain>(Guid.NewGuid());

            var castRef = grain.AsReference<IIndependentlyConcretizedGrain>();

            var result = await castRef.Hello();

            Assert.Equal("Hello!", result);
        }
        
        [Fact, TestCategory("BVT"), TestCategory("Cast"), TestCategory("Generics")]
        public async Task Generic_CastGenericInterfaceToNonGenericInterfaceBeforeActivation() {
            var grain =  this.GrainFactory.GetGrain<IGenericCastableGrain<string>>(Guid.NewGuid());

            var castRef = grain.AsReference<INonGenericCastGrain>();

            var result = await castRef.Hello();

            Assert.Equal("Hello!", result);
        }
        
        /// <summary>
        /// Tests that generic grains can have generic state and that the parameters to the Grain{TState}
        /// class do not have to match the parameters to the grain class itself.
        /// </summary>
        /// <returns></returns>
        [Fact, TestCategory("BVT"), TestCategory("Generics")]
        public async Task GenericGrainStateParameterMismatchTest()
        {
            var grain = this.GrainFactory.GetGrain<IGenericGrainWithGenericState<int, List<Guid>, string>>(Guid.NewGuid());
            var result = await grain.GetStateType();
            Assert.Equal(typeof(List<Guid>), result);
        }
    }

    namespace Generic.EdgeCases
    {
        using UnitTests.GrainInterfaces.Generic.EdgeCases;


        public class GenericEdgeCaseTests : HostedTestClusterEnsureDefaultStarted
        {
            public GenericEdgeCaseTests(DefaultClusterFixture fixture) : base(fixture)
            {
            }

            static async Task<Type[]> GetConcreteGenArgs(IBasicGrain @this) {
                var genArgTypeNames = await @this.ConcreteGenArgTypeNames();

                return genArgTypeNames.Select(n => Type.GetType(n))
                                        .ToArray();
            }
                        

            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_PartiallySpecifyingGenericGrainFulfilsInterface() {
                var grain =  this.GrainFactory.GetGrain<IGrainWithTwoGenArgs<string, int>>(Guid.NewGuid());

                var concreteGenArgs = await GetConcreteGenArgs(grain);

                Assert.True(
                        concreteGenArgs.SequenceEqual(new[] { typeof(int) })
                        );
            }
            

            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_GenericGrainCanReuseOwnGenArgRepeatedly() {
                //resolves correctly but can't be activated: too many gen args supplied for concrete class

                var grain =  this.GrainFactory.GetGrain<IGrainReceivingRepeatedGenArgs<int, int>>(Guid.NewGuid());

                var concreteGenArgs = await GetConcreteGenArgs(grain);

                Assert.True(
                        concreteGenArgs.SequenceEqual(new[] { typeof(int) })
                        );
            }


            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_PartiallySpecifyingGenericInterfaceIsCastable() {
                var grain =  this.GrainFactory.GetGrain<IPartiallySpecifyingInterface<string>>(Guid.NewGuid());

                await grain.Hello();

                var castRef = grain.AsReference<IGrainWithTwoGenArgs<string, int>>();

                var response = await castRef.Hello();

                Assert.Equal("Hello!", response);
            }


            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_PartiallySpecifyingGenericInterfaceIsCastable_Activating() {
                var grain =  this.GrainFactory.GetGrain<IPartiallySpecifyingInterface<string>>(Guid.NewGuid());

                var castRef = grain.AsReference<IGrainWithTwoGenArgs<string, int>>();

                var response = await castRef.Hello();

                Assert.Equal("Hello!", response);
            }
            

            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_RepeatedRearrangedGenArgsResolved() {
                //again resolves to the correct generic type definition, but fails on activation as too many args
                //gen args aren't being properly inferred from matched concrete type

                var grain =  this.GrainFactory.GetGrain<IReceivingRepeatedGenArgsAmongstOthers<int, string, int>>(Guid.NewGuid());

                var concreteGenArgs = await GetConcreteGenArgs(grain);

                Assert.True(
                        concreteGenArgs.SequenceEqual(new[] { typeof(string), typeof(int) })
                        );
            }
            

            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_RepeatedGenArgsWorkAmongstInterfacesInTypeResolution() {
                var grain =  this.GrainFactory.GetGrain<IReceivingRepeatedGenArgsFromOtherInterface<bool, bool, bool>>(Guid.NewGuid());

                var concreteGenArgs = await GetConcreteGenArgs(grain);

                Assert.True(
                        concreteGenArgs.SequenceEqual(Enumerable.Empty<Type>())
                        );
            }


            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_RepeatedGenArgsWorkAmongstInterfacesInCasting() {
                var grain =  this.GrainFactory.GetGrain<IReceivingRepeatedGenArgsFromOtherInterface<bool, bool, bool>>(Guid.NewGuid());

                await grain.Hello();

                var castRef = grain.AsReference<ISpecifyingGenArgsRepeatedlyToParentInterface<bool>>();

                var response = await castRef.Hello();

                Assert.Equal("Hello!", response);
            }


            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_RepeatedGenArgsWorkAmongstInterfacesInCasting_Activating() {
                //Only errors on invocation: wrong arity again

                var grain =  this.GrainFactory.GetGrain<IReceivingRepeatedGenArgsFromOtherInterface<bool, bool, bool>>(Guid.NewGuid());

                var castRef = grain.AsReference<ISpecifyingGenArgsRepeatedlyToParentInterface<bool>>();

                var response = await castRef.Hello();

                Assert.Equal("Hello!", response);
            }
            

            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_RearrangedGenArgsOfCorrectArityAreResolved() {
                var grain =  this.GrainFactory.GetGrain<IReceivingRearrangedGenArgs<int, long>>(Guid.NewGuid());

                var concreteGenArgs = await GetConcreteGenArgs(grain);

                Assert.True(
                        concreteGenArgs.SequenceEqual(new[] { typeof(long), typeof(int) })
                        );
            }
            

            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_RearrangedGenArgsOfCorrectNumberAreCastable() {
                var grain =  this.GrainFactory.GetGrain<ISpecifyingRearrangedGenArgsToParentInterface<int, long>>(Guid.NewGuid());

                await grain.Hello();

                var castRef = grain.AsReference<IReceivingRearrangedGenArgsViaCast<long, int>>();

                var response = await castRef.Hello();

                Assert.Equal("Hello!", response);
            }


            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_RearrangedGenArgsOfCorrectNumberAreCastable_Activating() {
                var grain =  this.GrainFactory.GetGrain<ISpecifyingRearrangedGenArgsToParentInterface<int, long>>(Guid.NewGuid());

                var castRef = grain.AsReference<IReceivingRearrangedGenArgsViaCast<long, int>>();

                var response = await castRef.Hello();

                Assert.Equal("Hello!", response);
            }



            //**************************************************************************************************************
            //**************************************************************************************************************

            //Below must be commented out, as supplying multiple fully-specified generic interfaces
            //to a class causes the codegen to fall over, stopping all other tests from working.

            //See new test here of the bit causing the issue - type info conflation:  
            //UnitTests.CodeGeneration.CodeGeneratorTests.CodeGen_EncounteredFullySpecifiedInterfacesAreEncodedDistinctly()


            //public interface IFullySpecifiedGenericInterface<T> : IBasicGrain
            //{ }

            //public interface IDerivedFromMultipleSpecializationsOfSameInterface : IFullySpecifiedGenericInterface<int>, IFullySpecifiedGenericInterface<long>
            //{ }

            //public class GrainFulfillingMultipleSpecializationsOfSameInterfaceViaIntermediate : BasicGrain, IDerivedFromMultipleSpecializationsOfSameInterface
            //{ }


            //[Fact, TestCategory("Generics")]
            //public async Task CastingBetweenFullySpecifiedGenericInterfaces() 
            //{
            //    //Is this legitimate? Solely in the realm of virtual grain interfaces - no special knowledge of implementation implicated, only of interface hierarchy

            //    //codegen falling over: duplicate key when both specializations are matched to same concrete type

            //    var grain =  this.GrainFactory.GetGrain<IDerivedFromMultipleSpecializationsOfSameInterface>(Guid.NewGuid());

            //    await grain.Hello();

            //    var castRef = grain.AsReference<IFullySpecifiedGenericInterface<int>>();

            //    await castRef.Hello();

            //    var castRef2 = castRef.AsReference<IFullySpecifiedGenericInterface<long>>();

            //    await castRef2.Hello();
            //}

            //*******************************************************************************************************
            

            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_CanCastToFullySpecifiedInterfaceUnrelatedToConcreteGenArgs() {
                var grain =  this.GrainFactory.GetGrain<IArbitraryInterface<int, long>>(Guid.NewGuid());

                await grain.Hello();

                _ = grain.AsReference<IInterfaceUnrelatedToConcreteGenArgs<float>>();

                var response = await grain.Hello();

                Assert.Equal("Hello!", response);
            }


            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_CanCastToFullySpecifiedInterfaceUnrelatedToConcreteGenArgs_Activating() {
                var grain =  this.GrainFactory.GetGrain<IArbitraryInterface<int, long>>(Guid.NewGuid());

                _ = grain.AsReference<IInterfaceUnrelatedToConcreteGenArgs<float>>();

                var response = await grain.Hello();

                Assert.Equal("Hello!", response);
            }
            

            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_GenArgsCanBeFurtherSpecialized() {
                var grain =  this.GrainFactory.GetGrain<IInterfaceTakingFurtherSpecializedGenArg<List<int>>>(Guid.NewGuid());

                var concreteGenArgs = await GetConcreteGenArgs(grain);

                Assert.True(
                        concreteGenArgs.SequenceEqual(new[] { typeof(int) })
                        );
            }


            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_GenArgsCanBeFurtherSpecializedIntoArrays() {
                var grain =  this.GrainFactory.GetGrain<IInterfaceTakingFurtherSpecializedGenArg<long[]>>(Guid.NewGuid());

                var concreteGenArgs = await GetConcreteGenArgs(grain);

                Assert.True(
                        concreteGenArgs.SequenceEqual(new[] { typeof(long) })
                        );
            }
            

            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_CanCastBetweenInterfacesWithFurtherSpecializedGenArgs() {
                var grain =  this.GrainFactory.GetGrain<IAnotherReceivingFurtherSpecializedGenArg<List<int>>>(Guid.NewGuid());

                await grain.Hello();
                _ = grain.AsReference<IYetOneMoreReceivingFurtherSpecializedGenArg<int[]>>();

                var response = await grain.Hello();

                Assert.Equal("Hello!", response);
            }


            [Fact(Skip = "Currently unsupported"), TestCategory("Generics")]
            public async Task Generic_CanCastBetweenInterfacesWithFurtherSpecializedGenArgs_Activating() {
                var grain =  this.GrainFactory.GetGrain<IAnotherReceivingFurtherSpecializedGenArg<List<int>>>(Guid.NewGuid());
                _ = grain.AsReference<IYetOneMoreReceivingFurtherSpecializedGenArg<int[]>>();

                var response = await grain.Hello();

                Assert.Equal("Hello!", response);
            }

        }


    }


}