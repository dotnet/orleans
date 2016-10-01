using System;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using Xunit;

// ReSharper disable ConvertToConstant.Local

namespace UnitTests.CodeGeneration
{
    /// <summary>
    /// Summary description for CodeGeneratorTests
    /// </summary>
    public class CodeGeneratorTests
    {
        [Fact, TestCategory("Functional"), TestCategory("CodeGen")]
        public void ServiceInterface_IsGrainClass()
        {
            Type t = typeof(Grain);
            Assert.False(TypeUtils.IsGrainClass(t), t.FullName + " is not grain class");
            t = typeof(Orleans.Runtime.GrainDirectory.RemoteGrainDirectory);
            Assert.False(TypeUtils.IsGrainClass(t), t.FullName + " should not be a grain class");
            Assert.True(TypeUtils.IsSystemTargetClass(t), t.FullName + " should be a system target class");
        }

        [Fact, TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void TypeUtils_RawClassName_Generic_1()
        {
            Type t = typeof(ISimpleGenericGrain1<>);
            Assert.Equal("ISimpleGenericGrain1`1", TypeUtils.GetRawClassName(TypeUtils.GetSimpleTypeName(t), t));
        }

        [Fact, TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void TypeUtils_RawClassName_Generic_2()
        {
            Type t = typeof(ISimpleGenericGrain2<,>);
            Assert.Equal("ISimpleGenericGrain2`2", TypeUtils.GetRawClassName(TypeUtils.GetSimpleTypeName(t), t));
        }

        [Fact, TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void TypeUtils_RawClassName_Generic_String_1()
        {
            string typeString = "GenericTestGrains.SimpleGenericGrain1`1[[System.Object, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]";
            Assert.Equal("GenericTestGrains.SimpleGenericGrain1`1", TypeUtils.GetRawClassName(typeString));
        }

        [Fact, TestCategory("Functional"), TestCategory("CodeGen")]
        public void IsGrainMethod()
        {
            Type t = typeof (ISimpleGrain);
            var meth = t.GetMethod("SetA");
            Assert.True(TypeUtils.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
            meth = t.GetMethod("GetA");
            Assert.True(TypeUtils.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
        }

        [Fact, TestCategory("Functional"), TestCategory("CodeGen")]
        public void IsTaskGrainMethod()
        {
            Type t = typeof(IEchoTaskGrain);
            var meth = t.GetMethod("EchoAsync");
            Assert.True(TypeUtils.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
            meth = t.GetMethod("EchoErrorAsync");
            Assert.True(TypeUtils.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
            meth = t.GetMethod("GetLastEchoAsync");
            Assert.True(TypeUtils.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
        }

        public interface IFullySpecified<T> : IGrain
        { }

        [Fact(Skip = "Currently unsupported"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void CodeGen_EncounteredFullySpecifiedInterfacesAreEncodedDistinctly() 
        {
            var id1 = GrainInterfaceUtils.ComputeInterfaceId(typeof(IFullySpecified<int>));
            var id2 = GrainInterfaceUtils.ComputeInterfaceId(typeof(IFullySpecified<long>));

            Assert.NotEqual(id1, id2);
        }
    }
}

// ReSharper restore ConvertToConstant.Local
