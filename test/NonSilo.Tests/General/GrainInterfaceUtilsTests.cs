using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Orleans;
using Orleans.CodeGeneration;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

namespace NonSilo.Tests.General
{
    [TestCategory("BVT")]
    public class GrainInterfaceUtilsTests
    {
        private readonly ITestOutputHelper _testOutputHelper;

        public GrainInterfaceUtilsTests(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
        }

        [Fact]
        public void Override_MethodId_Test()
        {
            var methodId = GrainInterfaceUtils.ComputeMethodId(
                typeof(IMethodInterceptionGrain).GetMethod(nameof(IMethodInterceptionGrain.One)));
            Assert.Equal(14142, methodId);

            methodId = GrainInterfaceUtils.ComputeMethodId(
                typeof(IMethodInterceptionGrain).GetMethod(nameof(IMethodInterceptionGrain.Echo)));
            Assert.Equal(-14142, methodId);
        }

        [Fact]
        public void Override_InterfaceId_Test()
        {
            var interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(typeof(IMethodInterceptionGrain));
            Assert.Equal(6548972, interfaceId);

            interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(typeof(IOutgoingMethodInterceptionGrain));
            Assert.Equal(-6548972, interfaceId);
        }

        [Fact]
        public void Generic_InterfaceId_Test()
        {
            var expected = GrainInterfaceUtils.GetGrainInterfaceId(typeof(ISimpleGenericGrain<>));
            var actual = GrainInterfaceUtils.GetRemoteInterfaces(typeof(SimpleGenericGrain<>));
            Assert.Single(actual);
            Assert.Equal(expected, GrainInterfaceUtils.GetGrainInterfaceId(actual[0]));
        }

        [Theory]
        [InlineData(typeof(ISimpleGrain), "UnitTests.GrainInterfaces.ISimpleGrain")]
        [InlineData(typeof(ISimpleGenericGrain<>), "UnitTests.GrainInterfaces.ISimpleGenericGrain`1<T>")]
        [InlineData(typeof(IGenericGrain<,>), "UnitTests.GrainInterfaces.IGenericGrain`2<T,U>")]

        [InlineData(typeof(IGenericGrainWithGenericState<,,>),
            "UnitTests.GrainInterfaces.IGenericGrainWithGenericState`3<TFirstTypeParam,TStateType,TLastTypeParam>")]

        [InlineData(typeof(Root<>.IA<,,>), "NonSilo.Tests.General.Root`1.IA`3")]
        public void GetFullName_Interface(Type interfaceType, string expected)
        {
            var actual = GrainInterfaceUtils.GetFullName(interfaceType);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(typeof(SimpleGrain), "UnitTests.GrainInterfaces.ISimpleGrain")]
        [InlineData(typeof(SimpleGenericGrain<>), "UnitTests.GrainInterfaces.ISimpleGenericGrain`1<T>")]
        [InlineData(typeof(SimpleGenericGrain2<,>), "UnitTests.GrainInterfaces.ISimpleGenericGrain2`2<T,U>")]

        [InlineData(typeof(ClosedGeneric),
            "NonSilo.Tests.General.IG2`2[[NonSilo.Tests.General.Dummy1, NonSilo.Tests, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null],[NonSilo.Tests.General.Dummy2, NonSilo.Tests, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null]]<Dummy1,Dummy2>")]

        [InlineData(typeof(ClosedGenericWithManyInterfaces),
            "NonSilo.Tests.General.IG2`2[[NonSilo.Tests.General.Dummy1, NonSilo.Tests, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null],[NonSilo.Tests.General.Dummy2, NonSilo.Tests, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null]]<Dummy1,Dummy2>",
            "NonSilo.Tests.General.IG2`2[[NonSilo.Tests.General.Dummy2, NonSilo.Tests, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null],[NonSilo.Tests.General.Dummy1, NonSilo.Tests, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null]]<Dummy2,Dummy1>")]

        [InlineData(typeof(GenericGrainWithGenericState<,,>), "UnitTests.GrainInterfaces.IGenericGrainWithGenericState`3<TFirstTypeParam,TStateType,TLastTypeParam>")]

        [InlineData(typeof(GenericReaderWriterGrain1<>),
            "UnitTests.GrainInterfaces.IGenericReaderWriterGrain1`1<T>",
            "UnitTests.GrainInterfaces.IGenericWriter1`1<T>",
            "UnitTests.GrainInterfaces.IGenericReader1`1<T>")]

        [InlineData(typeof(GenericReaderWriterGrain3<,,>),
            "UnitTests.GrainInterfaces.IGenericReaderWriterGrain3`3<TOne,TTwo,TThree>",
            "UnitTests.GrainInterfaces.IGenericWriter3`3<TOne,TTwo,TThree>",
            "UnitTests.GrainInterfaces.IGenericWriter2`2<TOne,TTwo>",
            "UnitTests.GrainInterfaces.IGenericReader3`3<TOne,TTwo,TThree>",
            "UnitTests.GrainInterfaces.IGenericReader2`2<TOne,TTwo>")]

        [InlineData(typeof(OpenGeneric<,>), "NonSilo.Tests.General.IG2`2<T1,T2>")]
        [InlineData(typeof(HalfOpenGrain1<>), "NonSilo.Tests.General.IG2`2<T1,Int32>")]
        [InlineData(typeof(HalfOpenGrain2<>), "NonSilo.Tests.General.IG2`2<Int32,T2>")]
        [InlineData(typeof(Root<>.G<,,>), "NonSilo.Tests.General.IG`1<Root<TRoot,T1,T2,T3>.IA>")]
        [InlineData(typeof(G1<,,,>), "NonSilo.Tests.General.Root`1.IA`3")]

        // broken logic for nested closed generic interfaces in generic class matching
        [InlineData(typeof(G1<bool, int, string, decimal>), "NonSilo.Tests.General.Root`1.IA`3")]
        public void GetFullName_ClassInterfaces(Type classType, params string[] expected)
        {
            var actual = classType
                .GetInterfaces()
                .Where(p => GrainInterfaceUtils.IsGrainInterface(p))
                .Select(p => GrainInterfaceUtils.GetFullName(p))
                .ToArray();

            _testOutputHelper.WriteLine("Expected: " + string.Join(";", expected));
            _testOutputHelper.WriteLine("Actual: " + string.Join(";", actual));

            Assert.Equal(expected, actual);
        }
    }

    public interface IG2<T1, T2> : IGrainWithGuidKey
    { }

    public class HalfOpenGrain1<T> : IG2<T, int>
    { }
    public class HalfOpenGrain2<T> : IG2<int, T>
    { }

    public class OpenGeneric<T2, T1> : IG2<T2, T1>
    { }

    public class ClosedGeneric : IG2<Dummy1, Dummy2>
    { }

    public class ClosedGenericWithManyInterfaces : IG2<Dummy1, Dummy2>, IG2<Dummy2, Dummy1>
    { }

    public class Dummy1 { }

    public class Dummy2 { }

    public interface IG<T> : IGrain
    {
    }

    public class G1<T1, T2, T3, T4> : Grain, Root<T1>.IA<T2, T3, T4>
    {
    }

    public class Root<TRoot>
    {
        public interface IA<T1, T2, T3> : IGrainWithIntegerKey
        {

        }

        public class G<T1, T2, T3> : Grain, IG<IA<T1, T2, T3>>
        {
        }
    }
}

