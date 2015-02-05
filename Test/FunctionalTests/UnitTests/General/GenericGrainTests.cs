using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTestGrainInterfaces.Generic;

namespace UnitTests.General
{
    [TestClass]
    public class GenericGrainTests : UnitTestBase
    {
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(10);
        private static int grainId = 0;

        public GenericGrainTests() : base(true)
        {
        }

        [TestCleanup]
        public void TestCleanup()
        {
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            //ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_SimpleGrain_GetGrain()
        {
            var grain = SimpleGenericGrainFactory<int>.GetGrain(grainId++);
            await grain.GetA();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainControlFlow()
        {
            var a = random.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var grain = SimpleGenericGrainFactory<int>.GetGrain(grainId++);

            await grain.SetA(a);

            await grain.SetB(b);

            Task<string> stringPromise = grain.GetAxB();
            Assert.AreEqual(expected, stringPromise.Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainDataFlow()
        {
            var a = random.Next(100);
            var b = a+1;
            var expected = a + "x" + b;
            
            ResultHandle result = new ResultHandle();

            var grain = SimpleGenericGrainFactory<int>.GetGrain(grainId++);

            var setAPromise = grain.SetA(a);
            var setBPromise = grain.SetB(b);
            var stringPromise = Task.WhenAll(setAPromise, setBPromise).ContinueWith((_) =>
            {
                return grain.GetAxB();
            }).Unwrap();

            var x = await stringPromise;
            result.Result = x;
            result.Done = true;

            Assert.IsTrue(result.WaitForFinished(timeout), "WaitforFinished Timeout=" + timeout);
            Assert.IsNotNull(result.Result, "Should not be null result");
            Assert.AreEqual(expected, result.Result, "Got expected result");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_SimpleGrain2_GetGrain()
        {
            var g1 = SimpleGenericGrainFactory<int>.GetGrain(grainId++);
            var g2 = SimpleGenericGrainUFactory<int>.GetGrain(grainId++);
            var g3 = SimpleGenericGrain2Factory<int, int>.GetGrain(grainId++);
            await g1.GetA();
            await g2.GetA();
            await g3.GetA();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainControlFlow2_SetAB()
        {
            var a = random.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var g1 = SimpleGenericGrainFactory<int>.GetGrain(grainId++);
            var g2 = SimpleGenericGrainUFactory<int>.GetGrain(grainId++);
            var g3 = SimpleGenericGrain2Factory<int, int>.GetGrain(grainId++);

            await g1.SetA(a);
            await g2.SetA(a);
            await g3.SetA(a);

            await g1.SetB(b);
            await g2.SetB(b);
            await g3.SetB(b);

            string r1 = await g1.GetAxB();
            string r2 = await g2.GetAxB();
            string r3 = await g3.GetAxB();
            Assert.AreEqual(expected, r1);
            Assert.AreEqual(expected, r2);
            Assert.AreEqual(expected, r3);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainControlFlow2_GetAB()
        {
            var a = random.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var g1 = SimpleGenericGrainFactory<int>.GetGrain(grainId++);
            var g2 = SimpleGenericGrainUFactory<int>.GetGrain(grainId++);
            var g3 = SimpleGenericGrain2Factory<int, int>.GetGrain(grainId++);

            string r1 = await g1.GetAxB(a, b);
            string r2 = await g2.GetAxB(a, b);
            string r3 = await g3.GetAxB(a, b);
            Assert.AreEqual(expected, r1, "Grain 1");
            Assert.AreEqual(expected, r2, "Grain 2");
            Assert.AreEqual(expected, r3, "Grain 3");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("Generics")]
        public async Task Generic_SimpleGrainControlFlow3()
        {
            ISimpleGenericGrain2<int, float> g = SimpleGenericGrain2Factory<int, float>.GetGrain(grainId++);
            await g.SetA(3);
            await g.SetB(1.25f);
            Assert.AreEqual("3x1.25", await g.GetAxB());
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("Generics")]
        public async Task Generic_SelfManagedGrainControlFlow()
        {
            IGenericSelfManagedGrain<int, float> g = GenericSelfManagedGrainFactory<int, float>.GetGrain(0);
            await g.SetA(3);
            await g.SetB(1.25f);
            Assert.AreEqual("3x1.25", await g.GetAxB());
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task GrainWithListFields()
        {
            string a = random.Next(100).ToString(CultureInfo.InvariantCulture);
            string b = random.Next(100).ToString(CultureInfo.InvariantCulture);

            Console.WriteLine("{0} a={1} b={2}", TestContext.TestName, a, b);

            var g1 = GrainWithListFieldsFactory.GetGrain(grainId++);

            var p1 = g1.AddItem(a);
            var p2  = g1.AddItem(b);
            await Task.WhenAll(p1,p2);

            var r1 = await g1.GetItems();

            Assert.IsTrue(
                (a == r1[0] && b == r1[1]) || (b == r1[0] && a == r1[1]), // Message ordering was not necessarily preserved.
                "Result: r[0]={0}, r[1]={1}", r1[0], r1[1]);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_GrainWithListFields()
        {
            int a = random.Next(100);
            int b = random.Next(100);

            Console.WriteLine("{0} a={1} b={2}", TestContext.TestName, a, b);

            var g1 = GenericGrainWithListFieldsFactory<int>.GetGrain(grainId++);

            var p1 = g1.AddItem(a);
            var p2 = g1.AddItem(b);
            await Task.WhenAll(p1, p2);

            var r1 = await g1.GetItems();

            Assert.IsTrue(
                (a == r1[0] && b == r1[1]) || (b == r1[0] && a == r1[1]), // Message ordering was not necessarily preserved.
                "Result: r[0]={0}, r[1]={1}", r1[0], r1[1]);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_GrainWithNoProperties_ControlFlow()
        {
            int a = random.Next(100);
            int b = random.Next(100);
            string expected = a + "x" + b;

            var g1 = GenericGrainWithNoPropertiesFactory<int>.GetGrain(grainId++);

            string r1 = await g1.GetAxB(a, b);
            Assert.AreEqual(expected, r1);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task GrainWithNoProperties_ControlFlow()
        {
            int a = random.Next(100);
            int b = random.Next(100);
            string expected = a + "x" + b;

            long grainId = GetRandomGrainId();
            var g1 = GrainWithNoPropertiesFactory.GetGrain(grainId);

            string r1 = await g1.GetAxB(a, b);
            Assert.AreEqual(expected, r1);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_ReaderWriterGrain1()
        {
            int a = random.Next(100);

            var g = GenericReaderWriterGrain1Factory<int>.GetGrain(grainId++);
            await g.SetValue(a);
            var res = await g.GetValue();
            Assert.AreEqual(a, res);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_ReaderWriterGrain2()
        {
            int a = random.Next(100);
            string b = "bbbbb";

            var g = GenericReaderWriterGrain2Factory<int, string>.GetGrain(grainId++);
            await g.SetValue1(a);
            await g.SetValue2(b);
            var r1 = await g.GetValue1();
            Assert.AreEqual(a, r1);
            var r2 = await g.GetValue2();
            Assert.AreEqual(b, r2);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_ReaderWriterGrain3()
        {
            int a = random.Next(100);
            string b = "bbbbb";
            double c = 3.145;

            var g = GenericReaderWriterGrain3Factory<int, string, double>.GetGrain(grainId++);
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

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_Non_Primitive_Type_Argument()
        {
            IEchoHubGrain<Guid, string> g1 = EchoHubGrainFactory<Guid, string>.GetGrain(1);
            IEchoHubGrain<Guid, int> g2 = EchoHubGrainFactory<Guid, int>.GetGrain(1);
            IEchoHubGrain<Guid, byte[]> g3 = EchoHubGrainFactory<Guid, byte[]>.GetGrain(1);
            
            Assert.AreNotEqual(((GrainReference)g1).GrainId, ((GrainReference)g2).GrainId);
            Assert.AreNotEqual(((GrainReference)g1).GrainId, ((GrainReference)g3).GrainId);
            Assert.AreNotEqual(((GrainReference)g2).GrainId, ((GrainReference)g3).GrainId);

            await g1.Foo(Guid.Empty, "", 1);
            await g2.Foo(Guid.Empty, 0, 2);
            await g3.Foo(Guid.Empty, new byte[]{}, 3);

            Assert.AreEqual(1, await g1.GetX());
            Assert.AreEqual(2, await g2.GetX());
            Assert.AreEqual(3m, await g3.GetX());
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_1()
        {
            const string msg1 = "Hello from EchoGenericChainGrain-1";

            IEchoGenericChainGrain<string> g1 = EchoGenericChainGrainFactory<string>.GetGrain(GetRandomGrainId());

            string received = await g1.Echo(msg1);
            Assert.AreEqual(msg1, received, "Echo");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_2()
        {
            const string msg2 = "Hello from EchoGenericChainGrain-2";

            IEchoGenericChainGrain<string> g2 = EchoGenericChainGrainFactory<string>.GetGrain(GetRandomGrainId());

            string received = await g2.Echo2(msg2);
            Assert.AreEqual(msg2, received, "Echo");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_Echo_Chain_3()
        {
            const string msg3 = "Hello from EchoGenericChainGrain-3";

            IEchoGenericChainGrain<string> g3 = EchoGenericChainGrainFactory<string>.GetGrain(GetRandomGrainId());

            string received = await g3.Echo3(msg3);
            Assert.AreEqual(msg3, received, "Echo");
        }


        [TestMethod, TestCategory("Nightly"), TestCategory("Generics")]
        public async Task Generic_1Argument_GenericCallOnly()
        {
            var grain = Generic1ArgumentFactory<string>.GetGrain(Guid.NewGuid(), "GenericTestGrains.Generic1ArgumentGrain");
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.Ping(s1);
            Assert.AreEqual(s1, s2);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Generics")]
        [ExpectedException(typeof(OrleansException))]
        public async Task Generic_1Argument_NonGenericCallFirst()
        {
            var id = Guid.NewGuid();
            var nonGenericFacet = NonGenericBaseFactory.GetGrain(id, "GenericTestGrains.Generic1ArgumentGrain");
            try
            {
                await nonGenericFacet.Ping();
            }
            catch (AggregateException exc)
            {
                throw exc.GetBaseException();
            }
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Generics")]
        [ExpectedException(typeof(OrleansException))]
        public async Task Generic_1Argument_GenericCallFirst()
        {
            var id = Guid.NewGuid();
            var grain = Generic1ArgumentFactory<string>.GetGrain(id, "GenericTestGrains.Generic1ArgumentGrain");
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.Ping(s1);
            Assert.AreEqual(s1, s2);
            var nonGenericFacet = NonGenericBaseFactory.GetGrain(id, "GenericTestGrains.Generic1ArgumentGrain");
            try
            {
                await nonGenericFacet.Ping();
            }
            catch (AggregateException exc)
            {
                throw exc.GetBaseException();
            }
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Generics")]
        public async Task DifferentTypeArgsProduceIndependentActivations()
        {
            var grain1 = DbGrainFactory<int>.GetGrain(0);
            await grain1.SetValue(123);

            var grain2 = DbGrainFactory<string>.GetGrain(0);
            var v = await grain2.GetValue();
            Assert.IsNull(v);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Generics"), TestCategory("Cast")]
        public async Task Generic_CastToSelf()
        {
            var id = Guid.NewGuid();
            var g = Generic1ArgumentFactory<string>.GetGrain(id, "GenericTestGrains.Generic1ArgumentGrain");
            var grain = Generic1ArgumentFactory<string>.Cast(g);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.Ping(s1);
            Assert.AreEqual(s1, s2);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Generics"), TestCategory("Echo")]
        public async Task Generic_PingSelf()
        {
            var id = Guid.NewGuid();
            var grain = GenericPingSelfFactory<string>.GetGrain(id);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.PingSelf(s1);
            Assert.AreEqual(s1, s2);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Generics"), TestCategory("Echo")]
        public async Task Generic_PingOther()
        {
            var id = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var grain = GenericPingSelfFactory<string>.GetGrain(id);
            var target = GenericPingSelfFactory<string>.GetGrain(targetId);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.PingOther(target, s1);
            Assert.AreEqual(s1, s2);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Generics"), TestCategory("Echo")]
        public async Task Generic_PingSelfThroughOther()
        {
            var id = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var grain = GenericPingSelfFactory<string>.GetGrain(id);
            var target = GenericPingSelfFactory<string>.GetGrain(targetId);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.PingSelfThroughOther(target, s1);
            Assert.AreEqual(s1, s2);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Generics"), TestCategory("ActivateDeactivate")]
        public async Task Generic_ScheduleDelayedPingAndDeactivate()
        {
            var id = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var grain = GenericPingSelfFactory<string>.GetGrain(id);
            var target = GenericPingSelfFactory<string>.GetGrain(targetId);
            var s1 = Guid.NewGuid().ToString();
            await grain.ScheduleDelayedPingToSelfAndDeactivate(target, s1, TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(6));
            var s2 = await grain.GetLastValue();
            Assert.AreEqual(s1, s2);
        }
    }
}
