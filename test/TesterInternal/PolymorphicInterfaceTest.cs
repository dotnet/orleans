using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using UnitTests.Tester;

namespace UnitTests.General
{
    public class PolymorphicInterfaceTest : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("Functional"), TestCategory("Cast")]
        public void Polymorphic_SimpleTest()
        {
            var grainFullName = typeof(PolymorphicTestGrain).FullName;
            IA IARef = GrainClient.GrainFactory.GetGrain<IA>(GetRandomGrainId(), grainFullName);
            Assert.AreEqual("A1", IARef.A1Method().Result);
            Assert.AreEqual("A2", IARef.A2Method().Result);
            Assert.AreEqual("A3", IARef.A3Method().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Cast")]
        public void Polymorphic_UpCastTest()
        {
            var grainFullName = typeof(PolymorphicTestGrain).FullName;
            IC ICRef = GrainClient.GrainFactory.GetGrain<IC>(GetRandomGrainId(), grainFullName);
            IA IARef = ICRef; // cast to polymorphic interface
            Assert.AreEqual("A1", IARef.A1Method().Result);
            Assert.AreEqual("A2", IARef.A2Method().Result);
            Assert.AreEqual("A3", IARef.A3Method().Result);

            IB IBRef = ICRef; // cast to polymorphic interface
            Assert.AreEqual("B1", IBRef.B1Method().Result);
            Assert.AreEqual("B2", IBRef.B2Method().Result);
            Assert.AreEqual("B3", IBRef.B3Method().Result);

            IF IFRef = GrainClient.GrainFactory.GetGrain<IF>(GetRandomGrainId(), grainFullName);

            Assert.AreEqual("F1", IFRef.F1Method().Result);
            Assert.AreEqual("F2", IFRef.F2Method().Result);
            Assert.AreEqual("F3", IFRef.F3Method().Result);

            IE IERef = IFRef; // cast to polymorphic interface
            Assert.AreEqual("E1", IERef.E1Method().Result);
            Assert.AreEqual("E2", IERef.E2Method().Result);
            Assert.AreEqual("E3", IERef.E3Method().Result);
            
        }

        [Fact, TestCategory("Functional"), TestCategory("Cast")]
        public void Polymorphic_FactoryMethods()
        {
            var grainFullName = typeof(PolymorphicTestGrain).FullName;
            IC ICRef = GrainClient.GrainFactory.GetGrain<IF>(GetRandomGrainId(), grainFullName); // FRef factory method returns a polymorphic reference to ICRef
            Assert.AreEqual("B2", ICRef.B2Method().Result);

            IA IARef = GrainClient.GrainFactory.GetGrain<ID>(GetRandomGrainId(), grainFullName); // DRef factory method returns a polymorphic reference to IARef
            Assert.AreEqual("A1", IARef.A1Method().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Cast")]
        public void Polymorphic_ServiceType()
        {
            var grainFullName = typeof(ServiceType).FullName;
            IServiceType serviceRef = GrainClient.GrainFactory.GetGrain<IServiceType>(GetRandomGrainId(), grainFullName);
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
        [Fact, TestCategory("Functional"), TestCategory("Cast")]
        public void Polymorphic__DerivedServiceType()
        {
            var grainFullName = typeof(DerivedServiceType).FullName;
            IDerivedServiceType derivedRef = GrainClient.GrainFactory.GetGrain<IDerivedServiceType>(GetRandomGrainId(), grainFullName);

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
