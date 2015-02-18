using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using UnitTestGrains;

namespace UnitTests.General
{
    [TestClass]
    public class GetGrainTests : UnitTestBase
    {
        public GetGrainTests()
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

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("GetGrain")]
        [ExpectedException(typeof(OrleansException))]
        public void GetGrain_Ambiguous()
        {
            var g = BaseFactory.GetGrain(GetRandomGrainId());
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("GetGrain")]
        public void GetGrain_Ambiguous_WithDefault()
        {
            var g = Base4Factory.GetGrain(GetRandomGrainId());
            Assert.IsFalse(g.Foo().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        public void GetGrain_WithFullName()
        {
            var g = BaseFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.BaseGrain");
            Assert.IsTrue(g.Foo().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        public void GetGrain_WithPrefix()
        {
            var g = BaseFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.Base");
            Assert.IsTrue(g.Foo().Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("GetGrain")]
        [ExpectedException(typeof(OrleansException))]
        public void GetGrain_AmbiguousPrefix()
        {
            var g = BaseFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("GetGrain")]
        [ExpectedException(typeof(ArgumentException))]
        public void GetGrain_WrongPrefix()
        {
            var g = BaseFactory.GetGrain(GetRandomGrainId(), "Foo");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        public void GetGrain_Derived_NoPrefix()
        {
            var g = DerivedFromBaseFactory.GetGrain(GetRandomGrainId());
            Assert.IsFalse(g.Foo().Result);
            Assert.IsTrue(g.Bar().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        public void GetGrain_Derived_WithFullName()
        {
            var g = DerivedFromBaseFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.DerivedFromBase");
            Assert.IsFalse(g.Foo().Result);
            Assert.IsTrue(g.Bar().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        public void GetGrain_Derived_WithFullName_FromBase()
        {
            var g = BaseFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.DerivedFromBase");
            Assert.IsFalse(g.Foo().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        public void GetGrain_Derived_WithPrefix()
        {
            var g = DerivedFromBaseFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains");
            Assert.IsFalse(g.Foo().Result);
            Assert.IsTrue(g.Bar().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        [ExpectedException(typeof(ArgumentException))]
        public void GetGrain_Derived_WithWrongPrefix()
        {
            var g = DerivedFromBaseFactory.GetGrain(GetRandomGrainId(), "Foo");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        public void GetGrain_OneImplementation_NoPrefix()
        {
            var g = Base1Factory.GetGrain(GetRandomGrainId());
            Assert.IsFalse(g.Foo().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        public void GetGrain_OneImplementation_Prefix()
        {
            var g = Base1Factory.GetGrain(GetRandomGrainId(), "UnitTestGrains.BaseGrain1");
            Assert.IsFalse(g.Foo().Result);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        public void GetGrain_MultipleUnrelatedInterfaces()
        {
            var g1 = Base3Factory.GetGrain(GetRandomGrainId());
            Assert.IsFalse(g1.Foo().Result);
            var g2 = Base2Factory.GetGrain(GetRandomGrainId());
            Assert.IsTrue(g2.Bar().Result);
        }
    }
}
