using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace DefaultCluster.Tests.General
{
    public class PolymorphicInterfaceTest : HostedTestClusterEnsureDefaultStarted
    {
        public PolymorphicInterfaceTest(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void Polymorphic_SimpleTest()
        {
            var grainFullName = typeof(PolymorphicTestGrain).FullName;
            IA IARef = this.GrainFactory.GetGrain<IA>(GetRandomGrainId(), grainFullName);
            Assert.Equal("A1", IARef.A1Method().Result);
            Assert.Equal("A2", IARef.A2Method().Result);
            Assert.Equal("A3", IARef.A3Method().Result);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void Polymorphic_UpCastTest()
        {
            var grainFullName = typeof(PolymorphicTestGrain).FullName;
            IC ICRef = this.GrainFactory.GetGrain<IC>(GetRandomGrainId(), grainFullName);
            IA IARef = ICRef; // cast to polymorphic interface
            Assert.Equal("A1", IARef.A1Method().Result);
            Assert.Equal("A2", IARef.A2Method().Result);
            Assert.Equal("A3", IARef.A3Method().Result);

            IB IBRef = ICRef; // cast to polymorphic interface
            Assert.Equal("B1", IBRef.B1Method().Result);
            Assert.Equal("B2", IBRef.B2Method().Result);
            Assert.Equal("B3", IBRef.B3Method().Result);

            IF IFRef = this.GrainFactory.GetGrain<IF>(GetRandomGrainId(), grainFullName);

            Assert.Equal("F1", IFRef.F1Method().Result);
            Assert.Equal("F2", IFRef.F2Method().Result);
            Assert.Equal("F3", IFRef.F3Method().Result);

            IE IERef = IFRef; // cast to polymorphic interface
            Assert.Equal("E1", IERef.E1Method().Result);
            Assert.Equal("E2", IERef.E2Method().Result);
            Assert.Equal("E3", IERef.E3Method().Result);
            
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void Polymorphic_FactoryMethods()
        {
            var grainFullName = typeof(PolymorphicTestGrain).FullName;
            IC ICRef = this.GrainFactory.GetGrain<IF>(GetRandomGrainId(), grainFullName); // FRef factory method returns a polymorphic reference to ICRef
            Assert.Equal("B2", ICRef.B2Method().Result);

            IA IARef = this.GrainFactory.GetGrain<ID>(GetRandomGrainId(), grainFullName); // DRef factory method returns a polymorphic reference to IARef
            Assert.Equal("A1", IARef.A1Method().Result);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void Polymorphic_ServiceType()
        {
            var grainFullName = typeof(ServiceType).FullName;
            IServiceType serviceRef = this.GrainFactory.GetGrain<IServiceType>(GetRandomGrainId(), grainFullName);
            Assert.Equal("A1", serviceRef.A1Method().Result);
            Assert.Equal("A2", serviceRef.A2Method().Result);
            Assert.Equal("A3", serviceRef.A3Method().Result);
            Assert.Equal("B1", serviceRef.B1Method().Result);
            Assert.Equal("B2", serviceRef.B2Method().Result);
            Assert.Equal("B3", serviceRef.B3Method().Result);
        }

        /// <summary>
        /// This unit test should consolidate all the use cases we are trying to cover with regard to polymorphic grain references
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void Polymorphic__DerivedServiceType()
        {
            var grainFullName = typeof(DerivedServiceType).FullName;
            IDerivedServiceType derivedRef = this.GrainFactory.GetGrain<IDerivedServiceType>(GetRandomGrainId(), grainFullName);

            IA IARef = derivedRef;
            Assert.Equal("A1", IARef.A1Method().Result);
            Assert.Equal("A2", IARef.A2Method().Result);
            Assert.Equal("A3", IARef.A3Method().Result);


            IB IBRef = (IB)IARef; // this could result in an invalid cast exception but it shoudn't because we have a priori knowledge that DerivedServiceType implements the interface
            Assert.Equal("B1", IBRef.B1Method().Result);
            Assert.Equal("B2", IBRef.B2Method().Result);
            Assert.Equal("B3", IBRef.B3Method().Result);

            IF IFRef = (IF)IBRef;
            Assert.Equal("F1", IFRef.F1Method().Result);
            Assert.Equal("F2", IFRef.F2Method().Result);
            Assert.Equal("F3", IFRef.F3Method().Result);

            IE IERef = (IE)IFRef;
            Assert.Equal("E1", IERef.E1Method().Result);
            Assert.Equal("E2", IERef.E2Method().Result);
            Assert.Equal("E3", IERef.E3Method().Result);

            IH IHRef = derivedRef;
            Assert.Equal("H1", IHRef.H1Method().Result);
            Assert.Equal("H2", IHRef.H2Method().Result);
            Assert.Equal("H3", IHRef.H3Method().Result);


            IServiceType serviceTypeRef = derivedRef; // upcast the pointer reference
            Assert.Equal("ServiceTypeMethod1", serviceTypeRef.ServiceTypeMethod1().Result);
            Assert.Equal("ServiceTypeMethod2", serviceTypeRef.ServiceTypeMethod2().Result);
            Assert.Equal("ServiceTypeMethod3", serviceTypeRef.ServiceTypeMethod3().Result);

            Assert.Equal("DerivedServiceTypeMethod1", derivedRef.DerivedServiceTypeMethod1().Result);
        }
    }
}
