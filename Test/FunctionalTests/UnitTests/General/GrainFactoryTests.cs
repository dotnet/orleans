using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;

using UnitTestGrainInterfaces;
using UnitTestGrainInterfaces.Generic;
using UnitTestGrains;

namespace UnitTests
{
    [TestClass]
    public class GrainFactoryTests : UnitTestBase
    {
        public GrainFactoryTests()
            : base(true)
        {
        }

        [TestCleanup]
        public void TestCleanup()
        {
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        [ExpectedException(typeof(OrleansException))]
        public void GetGrain_Ambiguous()
        {
            var g = GrainFactory.GetGrain<IBase>(GetRandomGrainId());
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Ambiguous_WithDefault()
        {
            var g = GrainFactory.GetGrain<IBase4>(GetRandomGrainId());
            Assert.IsFalse(g.Foo().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_WithFullName()
        {
            var g = GrainFactory.GetGrain<IBase>(GetRandomGrainId(), "UnitTestGrains.BaseGrain");
            Assert.IsTrue(g.Foo().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_WithPrefix()
        {
            var g = GrainFactory.GetGrain<IBase>(GetRandomGrainId(), "UnitTestGrains.Base");
            Assert.IsTrue(g.Foo().Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        [ExpectedException(typeof(OrleansException))]
        public void GetGrain_AmbiguousPrefix()
        {
            var g = GrainFactory.GetGrain<IBase>(GetRandomGrainId(), "UnitTestGrains");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        [ExpectedException(typeof(ArgumentException))]
        public void GetGrain_WrongPrefix()
        {
            var g = GrainFactory.GetGrain<IBase>(GetRandomGrainId(), "Foo");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Derived_NoPrefix()
        {
            var g = GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId());
            Assert.IsFalse(g.Foo().Result);
            Assert.IsTrue(g.Bar().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Derived_WithFullName()
        {
            var g = GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId(), "UnitTestGrains.DerivedFromBase");
            Assert.IsFalse(g.Foo().Result);
            Assert.IsTrue(g.Bar().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Derived_WithFullName_FromBase()
        {
            var g = GrainFactory.GetGrain<IBase>(GetRandomGrainId(), "UnitTestGrains.DerivedFromBase");
            Assert.IsFalse(g.Foo().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Derived_WithPrefix()
        {
            var g = GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId(), "UnitTestGrains");
            Assert.IsFalse(g.Foo().Result);
            Assert.IsTrue(g.Bar().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        [ExpectedException(typeof(ArgumentException))]
        public void GetGrain_Derived_WithWrongPrefix()
        {
            var g = GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId(), "Foo");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_OneImplementation_NoPrefix()
        {
            var g = GrainFactory.GetGrain<IBase1>(GetRandomGrainId());
            Assert.IsFalse(g.Foo().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_OneImplementation_Prefix()
        {
            var g = GrainFactory.GetGrain<IBase1>(GetRandomGrainId(), "UnitTestGrains.BaseGrain1");
            Assert.IsFalse(g.Foo().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_MultipleUnrelatedInterfaces()
        {
            var g1 = GrainFactory.GetGrain<IBase3>(GetRandomGrainId());
            Assert.IsFalse(g1.Foo().Result);
            var g2 = GrainFactory.GetGrain<IBase2>(GetRandomGrainId());
            Assert.IsTrue(g2.Bar().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetStringGrain()
        {
            var g = GrainFactory.GetGrain<IStringGrain>(Guid.NewGuid().ToString());
            Assert.IsTrue(g.Foo().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGuidGrain()
        {
            var g = GrainFactory.GetGrain<IGuidGrain>(Guid.NewGuid());
            Assert.IsTrue(g.Foo().Result);
        }
    }
}
