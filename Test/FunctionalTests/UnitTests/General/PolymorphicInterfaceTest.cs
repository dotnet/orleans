using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnitTestGrains;

namespace UnitTests.General
{
    [TestClass]
    public class PolymorphicInterfaceTest : UnitTestBase
    {
        //[ClassCleanup]
        //public static void MyClassCleanup()
        //{
        //    //ResetDefaultRuntimes();
        //}

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void Polymorphic_SimpleTest()
        {
            IA IARef = AFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.PolymorphicTestGrain");
            Assert.AreEqual("A1", IARef.A1Method().Result);
            Assert.AreEqual("A2", IARef.A2Method().Result);
            Assert.AreEqual("A3", IARef.A3Method().Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void Polymorphic_UpCastTest()
        {
            IC ICRef = CFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.PolymorphicTestGrain");
            IA IARef = ICRef; // cast to polymorphic interface
            Assert.AreEqual("A1", IARef.A1Method().Result);
            Assert.AreEqual("A2", IARef.A2Method().Result);
            Assert.AreEqual("A3", IARef.A3Method().Result);

            IB IBRef = ICRef; // cast to polymorphic interface
            Assert.AreEqual("B1", IBRef.B1Method().Result);
            Assert.AreEqual("B2", IBRef.B2Method().Result);
            Assert.AreEqual("B3", IBRef.B3Method().Result);

            IF IFRef = FFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.PolymorphicTestGrain");

            Assert.AreEqual("F1", IFRef.F1Method().Result);
            Assert.AreEqual("F2", IFRef.F2Method().Result);
            Assert.AreEqual("F3", IFRef.F3Method().Result);

            IE IERef = IFRef; // cast to polymorphic interface
            Assert.AreEqual("E1", IERef.E1Method().Result);
            Assert.AreEqual("E2", IERef.E2Method().Result);
            Assert.AreEqual("E3", IERef.E3Method().Result);
            
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void Polymorphic_FactoryMethods()
        {

            IC ICRef = FFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.PolymorphicTestGrain"); // FRef factory method returns a polymorphic reference to ICRef
            Assert.AreEqual("B2", ICRef.B2Method().Result);

            IA IARef = DFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.PolymorphicTestGrain"); // DRef factory method returns a polymorphic reference to IARef
            Assert.AreEqual("A1", IARef.A1Method().Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void Polymorphic_ServiceType()
        {
            IServiceType serviceRef = ServiceTypeFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.ServiceType");
            Assert.AreEqual("A1", serviceRef.A1Method().Result);
            Assert.AreEqual("A2", serviceRef.A2Method().Result);
            Assert.AreEqual("A3", serviceRef.A3Method().Result);
            Assert.AreEqual("B1", serviceRef.B1Method().Result);
            Assert.AreEqual("B2", serviceRef.B2Method().Result);
            Assert.AreEqual("B3", serviceRef.B3Method().Result);
        }

        /// <summary>
        /// This unit test should consolidate all the use cases we are trying to cover with regard to polymorphic grain references
        /// </summary>
        [TestMethod, TestCategory("Nightly"), TestCategory("Cast")]
        public void Polymorphic__DerivedServiceType()
        {
            IDerivedServiceType derivedRef = DerivedServiceTypeFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.DerivedServiceType");

            IA IARef = derivedRef;
            Assert.AreEqual("A1", IARef.A1Method().Result);
            Assert.AreEqual("A2", IARef.A2Method().Result);
            Assert.AreEqual("A3", IARef.A3Method().Result);


            IB IBRef = (IB)IARef; // this could result in an invalid cast exception but it shoudn't because we have a priori knowledge that DerivedServiceType implements the interface
            Assert.AreEqual("B1", IBRef.B1Method().Result);
            Assert.AreEqual("B2", IBRef.B2Method().Result);
            Assert.AreEqual("B3", IBRef.B3Method().Result);

            IF IFRef = (IF)IBRef;
            Assert.AreEqual("F1", IFRef.F1Method().Result);
            Assert.AreEqual("F2", IFRef.F2Method().Result);
            Assert.AreEqual("F3", IFRef.F3Method().Result);

            IE IERef = (IE)IFRef;
            Assert.AreEqual("E1", IERef.E1Method().Result);
            Assert.AreEqual("E2", IERef.E2Method().Result);
            Assert.AreEqual("E3", IERef.E3Method().Result);

            IH IHRef = derivedRef;
            Assert.AreEqual("H1", IHRef.H1Method().Result);
            Assert.AreEqual("H2", IHRef.H2Method().Result);
            Assert.AreEqual("H3", IHRef.H3Method().Result);


            IServiceType serviceTypeRef = derivedRef; // upcast the pointer reference
            Assert.AreEqual("ServiceTypeMethod1", serviceTypeRef.ServiceTypeMethod1().Result);
            Assert.AreEqual("ServiceTypeMethod2", serviceTypeRef.ServiceTypeMethod2().Result);
            Assert.AreEqual("ServiceTypeMethod3", serviceTypeRef.ServiceTypeMethod3().Result);

            Assert.AreEqual("DerivedServiceTypeMethod1", derivedRef.DerivedServiceTypeMethod1().Result);
        }
    }
}
