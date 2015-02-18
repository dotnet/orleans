using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.CodeGeneration;
using Orleans;
using Orleans.Runtime;
using UnitTestGrainInterfaces;
using UnitTestGrainInterfaces.Generic;
using UnitTestGrains;
using GrainInterfaceData = Orleans.CodeGeneration.GrainInterfaceData;
using UnitTests.GrainInterfaces;


// ReSharper disable ConvertToConstant.Local

namespace UnitTests.CodeGeneration
{
    /// <summary>
    /// Summary description for CodeGeneratorTests
    /// </summary>
    [TestClass]
    public class CodeGeneratorTests
    {
        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen")]
        public void ServiceInterface_IsGrainClass()
        {
            Type t = typeof(Grain);
            Assert.IsFalse(TypeUtils.IsGrainClass(t), t.FullName + " is not grain class");
            t = typeof(Orleans.Runtime.GrainDirectory.RemoteGrainDirectory);
            Assert.IsFalse(TypeUtils.IsGrainClass(t), t.FullName + " should not be a grain class");
            Assert.IsTrue(TypeUtils.IsSystemTargetClass(t), t.FullName + " should be a system target class");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen")]
        public void ServiceInterface_ServiceTypeName()
        {
            GrainInterfaceData si = new GrainInterfaceData(typeof(ISimpleGenericGrain<>));
            Assert.AreEqual("ISimpleGenericGrain<T>", si.TypeName, "TypeName [Client] = ISimpleGenericGrain<T>");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void ServiceInterface_Generic_ClassName()
        {
            GrainInterfaceData si = new GrainInterfaceData(typeof(ISimpleGenericGrain<>));
            Assert.AreEqual("ISimpleGenericGrain<T>", si.Name);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void ServiceInterface_Generic_FactoryClassName()
        {
            GrainInterfaceData si = new GrainInterfaceData(typeof(ISimpleGenericGrain<>));
            Assert.AreEqual("SimpleGenericGrainFactory<T>", si.FactoryClassName);
            Assert.AreEqual("SimpleGenericGrainFactory", si.FactoryClassBaseName);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void ServiceInterface_Generic_ReferenceClassName()
        {
            GrainInterfaceData si = new GrainInterfaceData(typeof(ISimpleGenericGrain<>));
            Assert.AreEqual("SimpleGenericGrainReference<T>", si.ReferenceClassName);
            Assert.AreEqual("SimpleGenericGrainReference", si.ReferenceClassBaseName);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void ServiceInterface_Generic_InvokerClassName()
        {
            GrainInterfaceData si = new GrainInterfaceData(typeof(ISimpleGenericGrain<>));
            Assert.AreEqual("SimpleGenericGrainMethodInvoker<T>", si.InvokerClassName);
            //Assert.AreEqual("SimpleGenericGrainMethodInvoker", interfaceData.InvokerClassBaseName);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void ServiceInterface_Generic_RemoteInterfaceName()
        {
            GrainInterfaceData si = new GrainInterfaceData(typeof(ISimpleGenericGrain<>));
            Assert.AreEqual("ISimpleGenericGrain<T>", si.InterfaceTypeName);
            Assert.AreEqual("ISimpleGenericGrain`1", si.Type.Name);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void ServiceInterface_Generic_RemoteInterfaceTypeName()
        {
            GrainInterfaceData si = new GrainInterfaceData(typeof(ISimpleGenericGrain<>));
            Assert.AreEqual("ISimpleGenericGrain<T>", si.InterfaceTypeName);
            //Assert.AreEqual("ISimpleGenericGrain", interfaceData.RemoteInterfaceTypeBaseName);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void ServiceInterface_Generic_GrainStateClassName()
        {
            GrainInterfaceData si = new GrainInterfaceData(typeof(ISimpleGenericGrain<>));
            Assert.AreEqual("SimpleGenericGrainState<T>", si.StateClassName);
            Assert.AreEqual("SimpleGenericGrainState", si.StateClassBaseName);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void TypeUtils_RawClassName_Generic_1()
        {
            Type t = typeof(ISimpleGenericGrain<>);
            Assert.AreEqual("ISimpleGenericGrain`1", TypeUtils.GetRawClassName(TypeUtils.GetSimpleTypeName(t), t));
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void TypeUtils_RawClassName_Generic_2()
        {
            Type t = typeof(ISimpleGenericGrain2<,>);
            Assert.AreEqual("ISimpleGenericGrain2`2", TypeUtils.GetRawClassName(TypeUtils.GetSimpleTypeName(t), t));
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void TypeUtils_RawClassName_Generic_String_1()
        {
            string typeString = "GenericTestGrains.SimpleGenericGrain`1[[System.Object, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]";
            Assert.AreEqual("GenericTestGrains.SimpleGenericGrain`1", TypeUtils.GetRawClassName(typeString));
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen")]
        public void IsGrainMethod()
        {
            Type t = typeof (ISimpleGrain);
            var meth = t.GetMethod("SetA");
            Assert.IsTrue(CodeGeneratorBase.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
            meth = t.GetMethod("GetA");
            Assert.IsTrue(CodeGeneratorBase.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen")]
        public void IsTaskGrainMethod()
        {
            Type t = typeof(Echo.IEchoTaskGrain);
            var meth = t.GetMethod("EchoAsync");
            Assert.IsTrue(CodeGeneratorBase.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
            meth = t.GetMethod("EchoErrorAsync");
            Assert.IsTrue(CodeGeneratorBase.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
            meth = t.GetMethod("GetLastEchoAsync");
            Assert.IsTrue(CodeGeneratorBase.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen")]
        public void CodeGen_AC_TaskGrain_GetInterfaceInfo()
        {
            Type t = typeof(Echo.IEchoTaskGrain);
            CodeGeneratorBase.GrainInterfaceInfo grainInterfaceInfo = CodeGeneratorBase.GetInterfaceInfo(t);
            Assert.AreEqual(1, grainInterfaceInfo.Interfaces.Count, "Expected one interface - EchoTaskGrain");
            Type interfaceType = grainInterfaceInfo.Interfaces.Values.First().InterfaceType;
            int interfaceId = grainInterfaceInfo.Interfaces.Keys.First();
            Assert.AreEqual(-1626135387, interfaceId, "InterfaceId - EchoTaskGrain");
            Assert.AreEqual(typeof(Echo.IEchoTaskGrain), interfaceType, "InterfaceType - EchoTaskGrain");

            t = typeof(Echo.IEchoGrain);
            grainInterfaceInfo = CodeGeneratorBase.GetInterfaceInfo(t);
            Assert.AreEqual(1, grainInterfaceInfo.Interfaces.Count, "Expected one interface");
            interfaceType = grainInterfaceInfo.Interfaces.Values.First().InterfaceType;
            interfaceId = grainInterfaceInfo.Interfaces.Keys.First();
            Assert.AreEqual(-2033891083, interfaceId, "InterfaceId");
            Assert.AreEqual(typeof(Echo.IEchoGrain), interfaceType, "InterfaceType");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen")]
        public void CodeGen_TaskGrain_GetInterfaceInfo()
        {
            Type t = typeof(Echo.IEchoTaskGrain);
            CodeGeneratorBase.GrainInterfaceInfo grainInterfaceInfo = CodeGeneratorBase.GetInterfaceInfo(t);
            Assert.AreEqual(1, grainInterfaceInfo.Interfaces.Count, "Expected one interface - Async");
            Type interfaceType = grainInterfaceInfo.Interfaces.Values.First().InterfaceType;
            int interfaceId = grainInterfaceInfo.Interfaces.Keys.First();
            Assert.AreEqual(-1626135387, interfaceId, "InterfaceId-Async");
            Assert.AreEqual(typeof(Echo.IEchoTaskGrain), interfaceType, "InterfaceType-Async");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen")]
        public void CodeGen_ObjectTo_List()
        {
            List<String> list = new List<string> { "1", "2" };

            ArrayList arrayList = new ArrayList(list);
            List<String> list2 = Utils.ObjectToList<string>(arrayList);
            CheckOutputList(list, list2);

            string[] array = list.ToArray();
            List<String> list3 = Utils.ObjectToList<string>(array);
            CheckOutputList(list, list3);

            List<string> listCopy = list.ToList();
            List<String> list4 = Utils.ObjectToList<string>(listCopy);
            CheckOutputList(list, list4);

            IReadOnlyList<string> readOnlyList = list.ToList();
            List<String> list5 = Utils.ObjectToList<string>(readOnlyList);
            CheckOutputList(list, list5);
        }

        private static void CheckOutputList(List<string> expected, List<string> actual)
        {
            Assert.AreEqual(expected.Count, actual.Count, "Output list size");
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.IsNotNull(actual[i], "Output list element #{0}", i);
                Assert.AreEqual(expected[i], actual[i], "Output list element #{0}", i);
            }
        }
    }

    [TestClass]
    public class CodeGeneratorTests_RequiringSilo : UnitTestBase
    {
        [ClassCleanup]
        public static void ClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        // These test cases create GrainReferences, to we need to be connected to silo for that to work.

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("UniqueKey")]
        public void CodeGen_GrainId_TypeCode()
        {
            ISimpleSelfManagedGrain g1 = SimpleSelfManagedGrainFactory.GetGrain(1);
            GrainId id1 = ((GrainReference)g1).GrainId;
            UniqueKey k1 = id1.Key;
            Assert.IsTrue(id1.IsGrain, "GrainReference should be for self-managed type");
            Assert.AreEqual(UniqueKey.Category.Grain, k1.IdCategory, "GrainId should be for self-managed type");
            Assert.AreEqual(1, k1.PrimaryKeyToLong(), "Encoded primary key should match");
            Assert.AreEqual(-1929503321, k1.BaseTypeCode, "Encoded type code data should match");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("CodeGen"), TestCategory("UniqueKey"), TestCategory("ActivationCollector")]
        public void CollectionTest_GrainId_TypeCode()
        {
            ICollectionTestGrain g1 = CollectionTestGrainFactory.GetGrain(1);
            GrainId id1 = ((GrainReference)g1).GrainId;
            UniqueKey k1 = id1.Key;
            Console.WriteLine("GrainId={0} UniqueKey={1} PK={2} KeyType={3} IdCategory={4}",
                id1, k1, id1.GetPrimaryKeyLong(), k1.IdCategory, k1.BaseTypeCode);
            Assert.IsTrue(id1.IsGrain, "GrainReference should be for self-managed type");
            Assert.AreEqual(UniqueKey.Category.Grain, k1.IdCategory, "GrainId should be for self-managed type");
            Assert.AreEqual(1, k1.PrimaryKeyToLong(), "Encoded primary key should match");
            Assert.AreEqual(-1096253375, k1.BaseTypeCode, "Encoded type code data should match");
        }
    }

}

// ReSharper restore ConvertToConstant.Local
