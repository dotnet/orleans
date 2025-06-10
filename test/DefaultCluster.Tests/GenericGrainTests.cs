using System.Globalization;
using Orleans.Runtime;
using TestExtensions;
using TestGrainInterfaces;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Comprehensive tests for Orleans' support of generic grains and interfaces.
    /// Validates that Orleans can correctly handle grain classes and interfaces with type parameters,
    /// including complex scenarios like multiple type parameters, inheritance hierarchies, type constraints,
    /// and proper grain activation/routing based on generic type arguments.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Generics")]
    public class GenericGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        private static int grainId = 0;

        public GenericGrainTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        public TGrainInterface GetGrain<TGrainInterface>(long i) where TGrainInterface : IGrainWithIntegerKey
        {
            return this.GrainFactory.GetGrain<TGrainInterface>(i);
        }

        public TGrainInterface GetGrain<TGrainInterface>() where TGrainInterface : IGrainWithIntegerKey
        {
            return this.GrainFactory.GetGrain<TGrainInterface>(GetRandomGrainId());
        }

        /// <summary>
        /// Tests that Orleans can instantiate multiple concrete grain types implementing different
        /// specializations of the same generic interface. Validates that type-specific grain
        /// implementations are correctly resolved and activated based on generic type arguments.
        /// </summary>
        [Fact]
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

        /// <summary>
        /// Validates grain identity preservation: multiple GetGrain calls with the same ID
        /// return references to the same grain activation. This ensures Orleans' single-activation
        /// guarantee holds for generic grains.
        /// </summary>
        [Fact]
        public async Task GenericGrainTests_ConcreteGrainWithGenericInterfaceMultiplicity()
        {
            var grainId = GetRandomGrainId();

            var grainRef1 = GetGrain<IGenericGrain<int, float>>(grainId);
            await grainRef1.SetT(123);

            var grainRef2 = GetGrain<IGenericGrain<int, float>>(grainId);
            var floatResult = await grainRef2.MapT2U();

            Assert.Equal(123f, floatResult);
        }

        /// <summary>
        /// Tests Orleans' ability to instantiate generic grain specializations with various type arguments.
        /// Uses theory-based testing to validate that generic grains work correctly with different
        /// primitive types (float, string), demonstrating type parameter flexibility.
        /// </summary>
        [Theory]
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

        /// <summary>
        /// Tests generic grains with array type parameters.
        /// Validates that Orleans can handle complex type arguments like arrays in generic grain interfaces.
        /// </summary>
        [Fact]
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

        /// <summary>
        /// Tests generic grains that work with arrays through inheritance.
        /// Validates Orleans' handling of generic grains that register and manage array-based state.
        /// </summary>
        [Fact]
        public async Task GenericGrainTests_GenericGrainInheritingArray()
        {
            var grain = GetGrain<IGenericArrayRegisterGrain<int>>();

            var expected = new[] { 1, 2, 3 };
            await grain.Set(expected);

            var result = await grain.Get();

            Assert.Equal(expected, result);
        }

        /// <summary>
        /// Tests grains implementing generic interfaces with nested generic type parameters.
        /// Validates Orleans' ability to handle complex generic scenarios like List<T> as a type argument.
        /// </summary>
        [Fact]
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


        /// <summary>
        /// Validates that multiple GetGrain calls for the same generic grain type and ID
        /// return the same activation. Ensures Orleans' single-activation semantics apply to generic grains.
        /// </summary>
        [Fact]
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

        /// <summary>
        /// Tests Orleans' type resolution preference rules: when both a concrete implementation
        /// and a generic implementation exist for a generic interface, Orleans should prefer
        /// the more specific concrete implementation. This validates proper type matching precedence.
        /// </summary>
        [Fact]
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

        /// <summary>
        /// Validates activation identity when Orleans prefers concrete implementations.
        /// Ensures that the preference for concrete implementations doesn't break
        /// the single-activation guarantee.
        /// </summary>
        [Fact]
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

        /// <summary>
        /// Tests grains implementing multiple generic interfaces.
        /// Validates that Orleans correctly handles grains with complex interface hierarchies
        /// involving multiple generic interfaces.
        /// </summary>
        [Fact]
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

        /// <summary>
        /// Validates single-activation semantics for grains with multiple interfaces.
        /// Tests that repeated GetGrain calls with the same interface return the same activation
        /// even when the grain implements multiple interfaces.
        /// </summary>
        [Fact]
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

        /// <summary>
        /// Tests grain identity across different interface references.
        /// Validates that accessing the same grain through different interfaces it implements
        /// still references the same underlying activation, maintaining state consistency.
        /// </summary>
        [Fact]
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

        /// <summary>
        /// Tests that grains can use the grain factory to create references to other generic grains.
        /// Validates that grain factory operations work correctly within grain code for generic types.
        /// </summary>
        [Fact]
        public async Task GenericGrainTests_UseGenericFactoryInsideGrain()
        {
            var grainId = GetRandomGrainId();

            var grainRef1 = GetGrain<ISimpleGenericGrain<string>>(grainId);
            await grainRef1.Set("JustString");
            await grainRef1.CompareGrainReferences(grainRef1);
        }


        [Fact]
        public async Task Generic_SimpleGrain_GetGrain()
        {
            var grain = this.GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);
            await grain.GetA();
        }

        [Fact]
        public async Task Generic_SimpleGrainControlFlow()
        {
            var a = Random.Shared.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var grain = this.GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);

            await grain.SetA(a);

            await grain.SetB(b);

            Task<string> stringPromise = grain.GetAxB();
            Assert.Equal(expected, await stringPromise);
        }

        [Fact]
        public void Generic_SimpleGrainControlFlow_Blocking()
        {
            var a = Random.Shared.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var grain = this.GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);

            // explicitly use .Wait() and .Result to make sure the client does not deadlock in these cases.
#pragma warning disable xUnit1031 // Do not use blocking task operations in test method
            grain.SetA(a).Wait();

            grain.SetB(b).Wait();

            Task<string> stringPromise = grain.GetAxB();
            Assert.Equal(expected, stringPromise.Result);
#pragma warning restore xUnit1031 // Do not use blocking task operations in test method
        }

        [Fact]
        public async Task Generic_SimpleGrainDataFlow()
        {
            var a = Random.Shared.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var grain = this.GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);

            var setAPromise = grain.SetA(a);
            var setBPromise = grain.SetB(b);
            var stringPromise = Task.WhenAll(setAPromise, setBPromise).ContinueWith((_) => grain.GetAxB()).Unwrap();

            var x = await stringPromise;
            Assert.Equal(expected, x);
        }

        [Fact]
        public async Task Generic_SimpleGrain2_GetGrain()
        {
            var g1 = this.GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);
            var g2 = this.GrainFactory.GetGrain<ISimpleGenericGrainU<int>>(grainId++);
            var g3 = this.GrainFactory.GetGrain<ISimpleGenericGrain2<int, int>>(grainId++);
            await g1.GetA();
            await g2.GetA();
            await g3.GetA();
        }

        [Fact]
        public async Task Generic_SimpleGrainGenericParameterWithMultipleArguments_GetGrain()
        {
            var g1 = this.GrainFactory.GetGrain<ISimpleGenericGrain1<Dictionary<int, int>>>(GetRandomGrainId());
            await g1.GetA();
        }

        [Fact]
        public async Task Generic_SimpleGrainControlFlow2_GetAB()
        {
            var a = Random.Shared.Next(100);
            var b = a + 1;
            var expected = a + "x" + b;

            var g1 = this.GrainFactory.GetGrain<ISimpleGenericGrain1<int>>(grainId++);
            var g2 = this.GrainFactory.GetGrain<ISimpleGenericGrainU<int>>(grainId++);
            var g3 = this.GrainFactory.GetGrain<ISimpleGenericGrain2<int, int>>(grainId++);

            string r1 = await g1.GetAxB(a, b);
            string r2 = await g2.GetAxB(a, b);
            string r3 = await g3.GetAxB(a, b);
            Assert.Equal(expected, r1);
            Assert.Equal(expected, r2);
            Assert.Equal(expected, r3);
        }

        [Fact]
        public async Task Generic_SimpleGrainControlFlow3()
        {
            ISimpleGenericGrain2<int, float> g = this.GrainFactory.GetGrain<ISimpleGenericGrain2<int, float>>(grainId++);
            await g.SetA(3);
            await g.SetB(1.25f);
            Assert.Equal("3x1.25", await g.GetAxB());
        }

        [Fact]
        public async Task Generic_BasicGrainControlFlow()
        {
            IBasicGenericGrain<int, float> g = this.GrainFactory.GetGrain<IBasicGenericGrain<int, float>>(0);
            await g.SetA(3);
            await g.SetB(1.25f);
            Assert.Equal("3x1.25", await g.GetAxB());
        }

        [Fact]
        public async Task GrainWithListFields()
        {
            string a = Random.Shared.Next(100).ToString(CultureInfo.InvariantCulture);
            string b = Random.Shared.Next(100).ToString(CultureInfo.InvariantCulture);

            var g1 = this.GrainFactory.GetGrain<IGrainWithListFields>(grainId++);

            var p1 = g1.AddItem(a);
            var p2 = g1.AddItem(b);
            await Task.WhenAll(p1, p2);

            var r1 = await g1.GetItems();

            Assert.True(
                (a == r1[0] && b == r1[1]) || (b == r1[0] && a == r1[1]), // Message ordering was not necessarily preserved.
                string.Format("Result: r[0]={0}, r[1]={1}", r1[0], r1[1]));
        }

        [Fact]
        public async Task Generic_GrainWithListFields()
        {
            int a = Random.Shared.Next(100);
            int b = Random.Shared.Next(100);


            var g1 = this.GrainFactory.GetGrain<IGenericGrainWithListFields<int>>(grainId++);

            var p1 = g1.AddItem(a);
            var p2 = g1.AddItem(b);
            await Task.WhenAll(p1, p2);

            var r1 = await g1.GetItems();

            Assert.True(
                (a == r1[0] && b == r1[1]) || (b == r1[0] && a == r1[1]), // Message ordering was not necessarily preserved.
                string.Format("Result: r[0]={0}, r[1]={1}", r1[0], r1[1]));
        }

        [Fact]
        public async Task Generic_GrainWithNoProperties_ControlFlow()
        {
            int a = Random.Shared.Next(100);
            int b = Random.Shared.Next(100);
            string expected = a + "x" + b;

            var g1 = this.GrainFactory.GetGrain<IGenericGrainWithNoProperties<int>>(grainId++);

            string r1 = await g1.GetAxB(a, b);
            Assert.Equal(expected, r1);
        }

        [Fact]
        public async Task GrainWithNoProperties_ControlFlow()
        {
            int a = Random.Shared.Next(100);
            int b = Random.Shared.Next(100);
            string expected = a + "x" + b;

            long grainId = GetRandomGrainId();
            var g1 = this.GrainFactory.GetGrain<IGrainWithNoProperties>(grainId);

            string r1 = await g1.GetAxB(a, b);
            Assert.Equal(expected, r1);
        }

        [Fact]
        public async Task Generic_ReaderWriterGrain1()
        {
            int a = Random.Shared.Next(100);
            var g = this.GrainFactory.GetGrain<IGenericReaderWriterGrain1<int>>(grainId++);
            await g.SetValue(a);
            var res = await g.GetValue();
            Assert.Equal(a, res);
        }

        [Fact]
        public async Task Generic_ReaderWriterGrain2()
        {
            int a = Random.Shared.Next(100);
            string b = "bbbbb";

            var g = this.GrainFactory.GetGrain<IGenericReaderWriterGrain2<int, string>>(grainId++);
            await g.SetValue1(a);
            await g.SetValue2(b);
            var r1 = await g.GetValue1();
            Assert.Equal(a, r1);
            var r2 = await g.GetValue2();
            Assert.Equal(b, r2);
        }

        [Fact]
        public async Task Generic_ReaderWriterGrain3()
        {
            int a = Random.Shared.Next(100);
            string b = "bbbbb";
            double c = 3.145;

            var g = this.GrainFactory.GetGrain<IGenericReaderWriterGrain3<int, string, double>>(grainId++);
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

        /// <summary>
        /// Tests generic grains with non-primitive type arguments like Guid and byte arrays.
        /// Validates that Orleans correctly differentiates grain activations based on complex
        /// generic type arguments, ensuring proper type-based routing.
        /// </summary>
        [Fact]
        public async Task Generic_Non_Primitive_Type_Argument()
        {
            IEchoHubGrain<Guid, string> g1 = this.GrainFactory.GetGrain<IEchoHubGrain<Guid, string>>(1);
            IEchoHubGrain<Guid, int> g2 = this.GrainFactory.GetGrain<IEchoHubGrain<Guid, int>>(1);
            IEchoHubGrain<Guid, byte[]> g3 = this.GrainFactory.GetGrain<IEchoHubGrain<Guid, byte[]>>(1);

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

        [Fact]
        public async Task Generic_Echo_Chain_1()
        {
            const string msg1 = "Hello from EchoGenericChainGrain-1";

            IEchoGenericChainGrain<string> g1 = this.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g1.Echo(msg1);
            Assert.Equal(msg1, received);
        }

        [Fact]
        public async Task Generic_Echo_Chain_2()
        {
            const string msg2 = "Hello from EchoGenericChainGrain-2";

            IEchoGenericChainGrain<string> g2 = this.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g2.Echo2(msg2);
            Assert.Equal(msg2, received);
        }

        [Fact]
        public async Task Generic_Echo_Chain_3()
        {
            const string msg3 = "Hello from EchoGenericChainGrain-3";

            IEchoGenericChainGrain<string> g3 = this.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g3.Echo3(msg3);
            Assert.Equal(msg3, received);
        }

        [Fact]
        public async Task Generic_Echo_Chain_4()
        {
            const string msg4 = "Hello from EchoGenericChainGrain-4";

            var g4 = this.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g4.Echo4(msg4);
            Assert.Equal(msg4, received);
        }

        [Fact]
        public async Task Generic_Echo_Chain_5()
        {
            const string msg5 = "Hello from EchoGenericChainGrain-5";

            var g5 = this.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g5.Echo5(msg5);
            Assert.Equal(msg5, received);
        }

        [Fact]
        public async Task Generic_Echo_Chain_6()
        {
            const string msg6 = "Hello from EchoGenericChainGrain-6";

            var g6 = this.GrainFactory.GetGrain<IEchoGenericChainGrain<string>>(GetRandomGrainId());

            string received = await g6.Echo6(msg6);
            Assert.Equal(msg6, received);
        }


        [Fact]
        public async Task Generic_1Argument_GenericCallOnly()
        {
            var grain = this.GrainFactory.GetGrain<IGeneric1Argument<string>>(Guid.NewGuid(), "UnitTests.Grains.Generic1ArgumentGrain");
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.Ping(s1);
            Assert.Equal(s1, s2);
        }

        [Fact]
        public void Generic_1Argument_NonGenericCallFirst()
        {
            Assert.Throws<ArgumentException>(() => this.GrainFactory.GetGrain<INonGenericBase>(Guid.NewGuid(), "UnitTests.Grains.Generic1ArgumentGrain"));
        }

        [Fact]
        public async Task Generic_1Argument_GenericCallFirst()
        {
            var id = Guid.NewGuid();
            var grain = this.GrainFactory.GetGrain<IGeneric1Argument<string>>(id, "UnitTests.Grains.Generic1ArgumentGrain");
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.Ping(s1);
            Assert.Equal(s1, s2);
            Assert.Throws<ArgumentException>(() => this.GrainFactory.GetGrain<INonGenericBase>(id, "UnitTests.Grains.Generic1ArgumentGrain"));
        }

        /// <summary>
        /// Validates that grains with different generic type arguments create independent activations.
        /// This is crucial for ensuring that IDbGrain<int> and IDbGrain<string> are treated as
        /// completely separate grain types with independent state.
        /// </summary>
        [Fact]
        public async Task DifferentTypeArgsProduceIndependentActivations()
        {
            var grain1 = this.GrainFactory.GetGrain<IDbGrain<int>>(0);
            await grain1.SetValue(123);

            var grain2 = this.GrainFactory.GetGrain<IDbGrain<string>>(0);
            var v = await grain2.GetValue();
            Assert.Null(v);
        }

        /// <summary>
        /// Tests generic grains making calls to themselves.
        /// Validates that self-referential calls work correctly in generic grain contexts.
        /// </summary>
        [Fact, TestCategory("Echo")]
        public async Task Generic_PingSelf()
        {
            var id = Guid.NewGuid();
            var grain = this.GrainFactory.GetGrain<IGenericPingSelf<string>>(id);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.PingSelf(s1);
            Assert.Equal(s1, s2);
        }

        /// <summary>
        /// Tests generic grains making calls to other generic grains of the same type.
        /// Validates grain-to-grain communication for generic grain types.
        /// </summary>
        [Fact, TestCategory("Echo")]
        public async Task Generic_PingOther()
        {
            var id = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var grain = this.GrainFactory.GetGrain<IGenericPingSelf<string>>(id);
            var target = this.GrainFactory.GetGrain<IGenericPingSelf<string>>(targetId);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.PingOther(target, s1);
            Assert.Equal(s1, s2);
        }

        /// <summary>
        /// Tests complex call chains where a generic grain calls itself through another grain.
        /// Validates that grain references remain valid when passed between generic grains.
        /// </summary>
        [Fact, TestCategory("Echo")]
        public async Task Generic_PingSelfThroughOther()
        {
            var id = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var grain = this.GrainFactory.GetGrain<IGenericPingSelf<string>>(id);
            var target = this.GrainFactory.GetGrain<IGenericPingSelf<string>>(targetId);
            var s1 = Guid.NewGuid().ToString();
            var s2 = await grain.PingSelfThroughOther(target, s1);
            Assert.Equal(s1, s2);
        }

        /// <summary>
        /// Tests scheduled operations and deactivation for generic grains.
        /// Validates that Orleans' activation lifecycle management works correctly with generic grains,
        /// including delayed operations and explicit deactivation.
        /// </summary>
        [Fact, TestCategory("ActivateDeactivate")]
        public async Task Generic_ScheduleDelayedPingAndDeactivate()
        {
            var id = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var grain = this.GrainFactory.GetGrain<IGenericPingSelf<string>>(id);
            var target = this.GrainFactory.GetGrain<IGenericPingSelf<string>>(targetId);
            var s1 = Guid.NewGuid().ToString();
            await grain.ScheduleDelayedPingToSelfAndDeactivate(target, s1, TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(6));
            var s2 = await grain.GetLastValue();
            Assert.Equal(s1, s2);
        }

        /// <summary>
        /// Tests serialization of generic grains with circular references in their state.
        /// Validates that Orleans' serialization system can handle complex object graphs
        /// in generic grain contexts without infinite loops.
        /// </summary>
        [Fact, TestCategory("Serialization")]
        public async Task SerializationTests_Generic_CircularReferenceTest()
        {
            var grainId = Guid.NewGuid();
            var grain = this.GrainFactory.GetGrain<ICircularStateTestGrain>(primaryKey: grainId, keyExtension: grainId.ToString("N"));
            _ = await grain.GetState();
        }

        /// <summary>
        /// Tests generic grains with type constraints (where T : class, new(), etc.).
        /// Validates that Orleans correctly enforces and works with C# generic type constraints,
        /// including unmanaged constraints and reference type constraints.
        /// </summary>
        [Fact]
        public async Task Generic_GrainWithTypeConstraints()
        {
            var grainId = Guid.NewGuid().ToString();
            var grain = this.GrainFactory.GetGrain<IGenericGrainWithConstraints<List<int>, int, string>>(grainId);
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

        /// <summary>
        /// Tests generic grains with value type state persistence.
        /// Validates that Orleans' persistence system correctly handles value types (structs)
        /// as grain state in generic grain contexts.
        /// </summary>
        [Fact, TestCategory("Persistence")]
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

        /// <summary>
        /// Tests casting non-generic grain references to generic interfaces after activation.
        /// Validates Orleans' support for late-bound interface discovery on already-activated grains.
        /// </summary>
        [Fact, TestCategory("Cast")]
        public async Task Generic_CastToGenericInterfaceAfterActivation()
        {
            var grain = this.GrainFactory.GetGrain<INonGenericCastableGrain>(Guid.NewGuid());
            await grain.DoSomething(); //activates original grain type here

            var castRef = grain.AsReference<ISomeGenericGrain<string>>();

            var result = await castRef.Hello();

            Assert.Equal("Hello!", result);
        }

        /// <summary>
        /// Tests casting to generic interfaces with different type arguments before activation.
        /// Validates that Orleans can handle interface casts that involve different generic
        /// type specializations before the grain is activated.
        /// </summary>
        [Fact, TestCategory("Cast")]
        public async Task Generic_CastToDifferentlyConcretizedGenericInterfaceBeforeActivation()
        {
            var grain = this.GrainFactory.GetGrain<INonGenericCastableGrain>(Guid.NewGuid());

            var castRef = grain.AsReference<IIndependentlyConcretizedGenericGrain<string>>();

            var result = await castRef.Hello();

            Assert.Equal("Hello!", result);
        }

        /// <summary>
        /// Tests casting between independently concretized generic interfaces.
        /// Validates Orleans' ability to handle complex interface hierarchies where interfaces
        /// are specialized independently of the grain's generic parameters.
        /// </summary>
        [Fact, TestCategory("Cast")]
        public async Task Generic_CastToDifferentlyConcretizedInterfaceBeforeActivation()
        {
            var grain = this.GrainFactory.GetGrain<INonGenericCastableGrain>(Guid.NewGuid());

            var castRef = grain.AsReference<IIndependentlyConcretizedGrain>();

            var result = await castRef.Hello();

            Assert.Equal("Hello!", result);
        }

        /// <summary>
        /// Tests casting from generic to non-generic interfaces.
        /// Validates that Orleans supports interface casts that cross the generic/non-generic boundary.
        /// </summary>
        [Fact, TestCategory("Cast")]
        public async Task Generic_CastGenericInterfaceToNonGenericInterfaceBeforeActivation()
        {
            var grain = this.GrainFactory.GetGrain<IGenericCastableGrain<string>>(Guid.NewGuid());

            var castRef = grain.AsReference<INonGenericCastGrain>();

            var result = await castRef.Hello();

            Assert.Equal("Hello!", result);
        }

        /// <summary>
        /// Tests that generic grains can have generic state with different type parameters.
        /// Validates that the type parameters for Grain<TState> don't need to match the grain's
        /// own type parameters, allowing flexible state modeling in generic grain implementations.
        /// </summary>
        [Fact]
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
        using UnitTests.Grains.Generic.EdgeCases;

        /// <summary>
        /// Tests for edge cases in Orleans' generic grain type system.
        /// Validates complex scenarios including partial type specialization, repeated type parameters,
        /// rearranged generic arguments, type parameter constraints in inheritance hierarchies,
        /// and advanced type inference scenarios that push the boundaries of the generic type resolver.
        /// </summary>
        [TestCategory("BVT"), TestCategory("Generics")]
        public class GenericEdgeCaseTests : HostedTestClusterEnsureDefaultStarted
        {
            public GenericEdgeCaseTests(DefaultClusterFixture fixture) : base(fixture)
            {
            }

            private static async Task<Type[]> GetConcreteGenArgs(IBasicGrain @this)
            {
                var genArgTypeNames = await @this.ConcreteGenArgTypeNames();
                return genArgTypeNames.Select(Type.GetType).ToArray();
            }

            /// <summary>
            /// Tests partial specialization of generic grain types.
            /// Validates Orleans' ability to handle grains that partially specify generic parameters,
            /// leaving some to be inferred from the interface implementation.
            /// Note: Currently unsupported feature.
            /// </summary>
            [Fact(Skip = "Currently unsupported")]
            public async Task Generic_PartiallySpecifyingGenericGrainFulfilsInterface()
            {
                var grain = this.GrainFactory.GetGrain<IGrainWithTwoGenArgs<string, int>>(Guid.NewGuid());
                var concreteGenArgs = await GetConcreteGenArgs(grain);
                Assert.True(concreteGenArgs.SequenceEqual(new[] { typeof(int) }));
            }

            /// <summary>
            /// Tests grains that reuse the same generic parameter multiple times.
            /// Validates handling of scenarios where a single type parameter appears
            /// multiple times in the grain's interface hierarchy.
            /// Note: Currently unsupported - fails with too many generic arguments.
            /// </summary>
            [Fact(Skip = "Currently unsupported")]
            public async Task Generic_GenericGrainCanReuseOwnGenArgRepeatedly()
            {
                // Resolves correctly but can't be activated: too many gen args supplied for concrete class
                var grain = this.GrainFactory.GetGrain<IGrainReceivingRepeatedGenArgs<int, int>>(Guid.NewGuid());
                var concreteGenArgs = await GetConcreteGenArgs(grain);
                Assert.True(concreteGenArgs.SequenceEqual(new[] { typeof(int) }));
            }

            /// <summary>
            /// Tests casting with partially specified generic interfaces.
            /// Validates that interfaces with partial generic specialization can be
            /// cast to their fully specified counterparts.
            /// </summary>
            [Fact]
            public async Task Generic_PartiallySpecifyingGenericInterfaceIsCastable()
            {
                var grain = this.GrainFactory.GetGrain<IPartiallySpecifyingInterface<string>>(Guid.NewGuid());
                await grain.Hello();
                var castRef = grain.AsReference<IGrainWithTwoGenArgs<string, int>>();
                var response = await castRef.Hello();
                Assert.Equal("Hello!", response);
            }

            /// <summary>
            /// Tests casting with partial generic specialization before activation.
            /// Validates that the casting mechanism works correctly even when the grain
            /// hasn't been activated yet.
            /// </summary>
            [Fact]
            public async Task Generic_PartiallySpecifyingGenericInterfaceIsCastable_Activating()
            {
                var grain = this.GrainFactory.GetGrain<IPartiallySpecifyingInterface<string>>(Guid.NewGuid());
                var castRef = grain.AsReference<IGrainWithTwoGenArgs<string, int>>();
                var response = await castRef.Hello();
                Assert.Equal("Hello!", response);
            }


            /// <summary>
            /// Tests resolution of generic arguments that are both repeated and rearranged.
            /// Validates complex type parameter mapping where arguments appear multiple times
            /// in different positions.
            /// Note: Currently unsupported - type inference fails.
            /// </summary>
            [Fact(Skip = "Currently unsupported")]
            public async Task Generic_RepeatedRearrangedGenArgsResolved()
            {
                // Again resolves to the correct generic type definition, but fails on activation as too many args
                // gen args aren't being properly inferred from matched concrete type
                var grain = this.GrainFactory.GetGrain<IReceivingRepeatedGenArgsAmongstOthers<int, string, int>>(Guid.NewGuid());
                var concreteGenArgs = await GetConcreteGenArgs(grain);
                Assert.True(concreteGenArgs.SequenceEqual(new[] { typeof(string), typeof(int) }));
            }

            /// <summary>
            /// Tests type resolution with repeated generic arguments across interfaces.
            /// Validates that Orleans correctly handles scenarios where the same type parameter
            /// is used multiple times across different interfaces in the hierarchy.
            /// </summary>
            [Fact]
            public async Task Generic_RepeatedGenArgsWorkAmongstInterfacesInTypeResolution()
            {
                var grain = this.GrainFactory.GetGrain<IReceivingRepeatedGenArgsFromOtherInterface<bool, bool, bool>>(Guid.NewGuid());
                var concreteGenArgs = await GetConcreteGenArgs(grain);
                Assert.True(concreteGenArgs.SequenceEqual(Enumerable.Empty<Type>()));
            }

            /// <summary>
            /// Tests casting between interfaces with repeated generic arguments.
            /// Validates that grains can be cast between interfaces that use the same
            /// type parameter multiple times in their definitions.
            /// </summary>
            [Fact]
            public async Task Generic_RepeatedGenArgsWorkAmongstInterfacesInCasting()
            {
                var grain = this.GrainFactory.GetGrain<IReceivingRepeatedGenArgsFromOtherInterface<bool, bool, bool>>(Guid.NewGuid());
                await grain.Hello();
                var castRef = grain.AsReference<ISpecifyingGenArgsRepeatedlyToParentInterface<bool>>();
                var response = await castRef.Hello();
                Assert.Equal("Hello!", response);
            }

            /// <summary>
            /// Tests casting with repeated generic arguments before activation.
            /// Validates pre-activation casting behavior for complex generic hierarchies
            /// with repeated type parameters.
            /// </summary>
            [Fact]
            public async Task Generic_RepeatedGenArgsWorkAmongstInterfacesInCasting_Activating()
            {
                var grain = this.GrainFactory.GetGrain<IReceivingRepeatedGenArgsFromOtherInterface<bool, bool, bool>>(Guid.NewGuid());
                var castRef = grain.AsReference<ISpecifyingGenArgsRepeatedlyToParentInterface<bool>>();
                var response = await castRef.Hello();
                Assert.Equal("Hello!", response);
            }

            /// <summary>
            /// Tests resolution of rearranged generic arguments.
            /// Validates that Orleans can handle interfaces where generic parameters
            /// appear in different orders than in the grain implementation.
            /// Note: Currently unsupported feature.
            /// </summary>
            [Fact(Skip = "Currently unsupported")]
            public async Task Generic_RearrangedGenArgsOfCorrectArityAreResolved()
            {
                var grain = this.GrainFactory.GetGrain<IReceivingRearrangedGenArgs<int, long>>(Guid.NewGuid());
                var concreteGenArgs = await GetConcreteGenArgs(grain);
                Assert.True(concreteGenArgs.SequenceEqual(new[] { typeof(long), typeof(int) }));
            }

            /// <summary>
            /// Tests casting between interfaces with rearranged generic arguments.
            /// Validates that casting works when generic parameters are in different
            /// positions but the total count matches.
            /// </summary>
            [Fact]
            public async Task Generic_RearrangedGenArgsOfCorrectNumberAreCastable()
            {
                var grain = this.GrainFactory.GetGrain<ISpecifyingRearrangedGenArgsToParentInterface<int, long>>(Guid.NewGuid());
                await grain.Hello();
                var castRef = grain.AsReference<IReceivingRearrangedGenArgsViaCast<long, int>>();
                var response = await castRef.Hello();
                Assert.Equal("Hello!", response);
            }

            /// <summary>
            /// Tests pre-activation casting with rearranged generic arguments.
            /// Validates that parameter rearrangement doesn't break casting before
            /// the grain is activated.
            /// </summary>
            [Fact]
            public async Task Generic_RearrangedGenArgsOfCorrectNumberAreCastable_Activating()
            {
                var grain = this.GrainFactory.GetGrain<ISpecifyingRearrangedGenArgsToParentInterface<int, long>>(Guid.NewGuid());
                var castRef = grain.AsReference<IReceivingRearrangedGenArgsViaCast<long, int>>();
                var response = await castRef.Hello();
                Assert.Equal("Hello!", response);
            }

            public interface IFullySpecifiedGenericInterface<T> : IBasicGrain
            { }

            public interface IDerivedFromMultipleSpecializationsOfSameInterface : IFullySpecifiedGenericInterface<int>, IFullySpecifiedGenericInterface<long>
            { }

            public class GrainFulfillingMultipleSpecializationsOfSameInterfaceViaIntermediate : BasicGrain, IDerivedFromMultipleSpecializationsOfSameInterface
            { }

            /// <summary>
            /// Tests casting between multiple specializations of the same generic interface.
            /// Validates that a grain implementing multiple concrete versions of a generic
            /// interface can be cast between those specializations.
            /// </summary>
            [Fact]
            public async Task CastingBetweenFullySpecifiedGenericInterfaces()
            {
                var grain = this.GrainFactory.GetGrain<IDerivedFromMultipleSpecializationsOfSameInterface>(Guid.NewGuid());
                await grain.Hello();
                var castRef = grain.AsReference<IFullySpecifiedGenericInterface<int>>();
                await castRef.Hello();
                var castRef2 = castRef.AsReference<IFullySpecifiedGenericInterface<long>>();
                await castRef2.Hello();
            }

            /// <summary>
            /// Tests casting to interfaces with unrelated generic parameters.
            /// Validates that grains can be cast to interfaces whose generic parameters
            /// have no relationship to the grain's concrete type arguments.
            /// </summary>
            [Fact]
            public async Task Generic_CanCastToFullySpecifiedInterfaceUnrelatedToConcreteGenArgs()
            {
                var grain = this.GrainFactory.GetGrain<IArbitraryInterface<int, long>>(Guid.NewGuid());
                await grain.Hello();
                _ = grain.AsReference<IInterfaceUnrelatedToConcreteGenArgs<float>>();
                var response = await grain.Hello();
                Assert.Equal("Hello!", response);
            }


            /// <summary>
            /// Tests pre-activation casting to unrelated generic interfaces.
            /// Validates that casting to interfaces with independent generic parameters
            /// works before grain activation.
            /// </summary>
            [Fact]
            public async Task Generic_CanCastToFullySpecifiedInterfaceUnrelatedToConcreteGenArgs_Activating()
            {
                var grain = this.GrainFactory.GetGrain<IArbitraryInterface<int, long>>(Guid.NewGuid());
                _ = grain.AsReference<IInterfaceUnrelatedToConcreteGenArgs<float>>();
                var response = await grain.Hello();
                Assert.Equal("Hello!", response);
            }

            /// <summary>
            /// Tests further specialization of already-generic type arguments.
            /// Validates handling of nested generics like List<T> where T itself is
            /// a type parameter to be resolved.
            /// Note: Currently unsupported feature.
            /// </summary>
            [Fact(Skip = "Currently unsupported")]
            public async Task Generic_GenArgsCanBeFurtherSpecialized()
            {
                var grain = this.GrainFactory.GetGrain<IInterfaceTakingFurtherSpecializedGenArg<List<int>>>(Guid.NewGuid());
                var concreteGenArgs = await GetConcreteGenArgs(grain);
                Assert.True(concreteGenArgs.SequenceEqual(new[] { typeof(int) }));
            }

            /// <summary>
            /// Tests specialization of generic parameters into array types.
            /// Validates that Orleans can handle scenarios where generic parameters
            /// are specialized as array types (T[]).
            /// Note: Currently unsupported feature.
            /// </summary>
            [Fact(Skip = "Currently unsupported")]
            public async Task Generic_GenArgsCanBeFurtherSpecializedIntoArrays()
            {
                var grain = this.GrainFactory.GetGrain<IInterfaceTakingFurtherSpecializedGenArg<long[]>>(Guid.NewGuid());
                var concreteGenArgs = await GetConcreteGenArgs(grain);
                Assert.True(concreteGenArgs.SequenceEqual(new[] { typeof(long) }));
            }

            /// <summary>
            /// Tests casting between interfaces with nested generic specializations.
            /// Validates casting when interfaces use complex generic types like List<T>
            /// with different but compatible specializations.
            /// Note: Currently unsupported feature.
            /// </summary>
            [Fact(Skip = "Currently unsupported")]
            public async Task Generic_CanCastBetweenInterfacesWithFurtherSpecializedGenArgs()
            {
                var grain = this.GrainFactory.GetGrain<IAnotherReceivingFurtherSpecializedGenArg<List<int>>>(Guid.NewGuid());
                await grain.Hello();
                _ = grain.AsReference<IYetOneMoreReceivingFurtherSpecializedGenArg<int[]>>();
                var response = await grain.Hello();
                Assert.Equal("Hello!", response);
            }

            /// <summary>
            /// Tests pre-activation casting with nested generic specializations.
            /// Validates that complex generic casting scenarios work before grain activation.
            /// Note: Currently unsupported feature.
            /// </summary>
            [Fact(Skip = "Currently unsupported")]
            public async Task Generic_CanCastBetweenInterfacesWithFurtherSpecializedGenArgs_Activating()
            {
                var grain = this.GrainFactory.GetGrain<IAnotherReceivingFurtherSpecializedGenArg<List<int>>>(Guid.NewGuid());
                _ = grain.AsReference<IYetOneMoreReceivingFurtherSpecializedGenArg<int[]>>();

                var response = await grain.Hello();

                Assert.Equal("Hello!", response);
            }
        }
    }
}