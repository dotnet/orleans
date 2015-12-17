using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
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
        [TestMethod, TestCategory("Functional"), TestCategory("CodeGen")]
        public void ServiceInterface_IsGrainClass()
        {
            Type t = typeof(Grain);
            Assert.IsFalse(TypeUtils.IsGrainClass(t), t.FullName + " is not grain class");
            t = typeof(Orleans.Runtime.GrainDirectory.RemoteGrainDirectory);
            Assert.IsFalse(TypeUtils.IsGrainClass(t), t.FullName + " should not be a grain class");
            Assert.IsTrue(TypeUtils.IsSystemTargetClass(t), t.FullName + " should be a system target class");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void TypeUtils_RawClassName_Generic_1()
        {
            Type t = typeof(ISimpleGenericGrain1<>);
            Assert.AreEqual("ISimpleGenericGrain1`1", TypeUtils.GetRawClassName(TypeUtils.GetSimpleTypeName(t), t));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void TypeUtils_RawClassName_Generic_2()
        {
            Type t = typeof(ISimpleGenericGrain2<,>);
            Assert.AreEqual("ISimpleGenericGrain2`2", TypeUtils.GetRawClassName(TypeUtils.GetSimpleTypeName(t), t));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Generics")]
        public void TypeUtils_RawClassName_Generic_String_1()
        {
            string typeString = "GenericTestGrains.SimpleGenericGrain1`1[[System.Object, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]";
            Assert.AreEqual("GenericTestGrains.SimpleGenericGrain1`1", TypeUtils.GetRawClassName(typeString));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("CodeGen")]
        public void IsGrainMethod()
        {
            Type t = typeof (ISimpleGrain);
            var meth = t.GetMethod("SetA");
            Assert.IsTrue(TypeUtils.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
            meth = t.GetMethod("GetA");
            Assert.IsTrue(TypeUtils.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("CodeGen")]
        public void IsTaskGrainMethod()
        {
            Type t = typeof(IEchoTaskGrain);
            var meth = t.GetMethod("EchoAsync");
            Assert.IsTrue(TypeUtils.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
            meth = t.GetMethod("EchoErrorAsync");
            Assert.IsTrue(TypeUtils.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
            meth = t.GetMethod("GetLastEchoAsync");
            Assert.IsTrue(TypeUtils.IsGrainMethod(meth), "Method " + meth.DeclaringType + "." + meth.Name + " should be a grain method");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("CodeGen")]
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
}

// ReSharper restore ConvertToConstant.Local
