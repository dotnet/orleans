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
        public async Task Polymorphic_SimpleTest()
        {
            var grainFullName = typeof(PolymorphicTestGrain).FullName;
            IA IARef = this.GrainFactory.GetGrain<IA>(GetRandomGrainId(), grainFullName);
            Assert.Equal("A1", await IARef.A1Method());
            Assert.Equal("A2", await IARef.A2Method());
            Assert.Equal("A3", await IARef.A3Method());
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Polymorphic_UpCastTest()
        {
            var grainFullName = typeof(PolymorphicTestGrain).FullName;
            IC ICRef = this.GrainFactory.GetGrain<IC>(GetRandomGrainId(), grainFullName);
            IA IARef = ICRef; // cast to polymorphic interface
            Assert.Equal("A1", await IARef.A1Method());
            Assert.Equal("A2", await IARef.A2Method());
            Assert.Equal("A3", await IARef.A3Method());

            IB IBRef = ICRef; // cast to polymorphic interface
            Assert.Equal("B1", await IBRef.B1Method());
            Assert.Equal("B2", await IBRef.B2Method());
            Assert.Equal("B3", await IBRef.B3Method());

            IF IFRef = this.GrainFactory.GetGrain<IF>(GetRandomGrainId(), grainFullName);

            Assert.Equal("F1", await IFRef.F1Method());
            Assert.Equal("F2", await IFRef.F2Method());
            Assert.Equal("F3", await IFRef.F3Method());

            IE IERef = IFRef; // cast to polymorphic interface
            Assert.Equal("E1", await IERef.E1Method());
            Assert.Equal("E2", await IERef.E2Method());
            Assert.Equal("E3", await IERef.E3Method());
            
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Polymorphic_FactoryMethods()
        {
            var grainFullName = typeof(PolymorphicTestGrain).FullName;
            IC ICRef = this.GrainFactory.GetGrain<IF>(GetRandomGrainId(), grainFullName); // FRef factory method returns a polymorphic reference to ICRef
            Assert.Equal("B2", await ICRef.B2Method());

            IA IARef = this.GrainFactory.GetGrain<ID>(GetRandomGrainId(), grainFullName); // DRef factory method returns a polymorphic reference to IARef
            Assert.Equal("A1", await IARef.A1Method());
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Polymorphic_ServiceType()
        {
            var grainFullName = typeof(ServiceType).FullName;
            IServiceType serviceRef = this.GrainFactory.GetGrain<IServiceType>(GetRandomGrainId(), grainFullName);
            Assert.Equal("A1", await serviceRef.A1Method());
            Assert.Equal("A2", await serviceRef.A2Method());
            Assert.Equal("A3", await serviceRef.A3Method());
            Assert.Equal("B1", await serviceRef.B1Method());
            Assert.Equal("B2", await serviceRef.B2Method());
            Assert.Equal("B3", await serviceRef.B3Method());
        }

        /// <summary>
        /// This unit test should consolidate all the use cases we are trying to cover with regard to polymorphic grain references
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Polymorphic__DerivedServiceType()
        {
            var grainFullName = typeof(DerivedServiceType).FullName;
            IDerivedServiceType derivedRef = this.GrainFactory.GetGrain<IDerivedServiceType>(GetRandomGrainId(), grainFullName);

            IA IARef = derivedRef;
            Assert.Equal("A1", await IARef.A1Method());
            Assert.Equal("A2", await IARef.A2Method());
            Assert.Equal("A3", await IARef.A3Method());


            IB IBRef = (IB)IARef; // this could result in an invalid cast exception but it shoudn't because we have a priori knowledge that DerivedServiceType implements the interface
            Assert.Equal("B1", await IBRef.B1Method());
            Assert.Equal("B2", await IBRef.B2Method());
            Assert.Equal("B3", await IBRef.B3Method());

            IF IFRef = (IF)IBRef;
            Assert.Equal("F1", await IFRef.F1Method());
            Assert.Equal("F2", await IFRef.F2Method());
            Assert.Equal("F3", await IFRef.F3Method());

            IE IERef = (IE)IFRef;
            Assert.Equal("E1", await IERef.E1Method());
            Assert.Equal("E2", await IERef.E2Method());
            Assert.Equal("E3", await IERef.E3Method());

            IH IHRef = derivedRef;
            Assert.Equal("H1", await IHRef.H1Method());
            Assert.Equal("H2", await IHRef.H2Method());
            Assert.Equal("H3", await IHRef.H3Method());

            IServiceType serviceTypeRef = derivedRef; // upcast the pointer reference
            Assert.Equal("ServiceTypeMethod1", await serviceTypeRef.ServiceTypeMethod1());
            Assert.Equal("ServiceTypeMethod2", await serviceTypeRef.ServiceTypeMethod2());
            Assert.Equal("ServiceTypeMethod3", await serviceTypeRef.ServiceTypeMethod3());

            Assert.Equal("DerivedServiceTypeMethod1", await derivedRef.DerivedServiceTypeMethod1());
        }
    }
}
