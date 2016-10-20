﻿using System;
using Orleans;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests
{
    public class GrainFactoryTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Ambiguous()
        {
            Xunit.Assert.Throws(typeof(OrleansException), () =>
            {
                var g = GrainClient.GrainFactory.GetGrain<IBase>(GetRandomGrainId());
            });
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Ambiguous_WithDefault()
        {
            var g = GrainClient.GrainFactory.GetGrain<IBase4>(GetRandomGrainId());
            Assert.False(g.Foo().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_WithFullName()
        {
            var grainFullName = typeof(BaseGrain).FullName;
            var g = GrainClient.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), grainFullName);
            Assert.True(g.Foo().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_WithPrefix()
        {
            var g = GrainClient.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), BaseGrain.GrainPrefix);
            Assert.True(g.Foo().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_AmbiguousPrefix()
        {
            Xunit.Assert.Throws(typeof(OrleansException), () =>
            {
                var g = GrainClient.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), "UnitTests.Grains");
            });
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_WrongPrefix()
        {
            Xunit.Assert.Throws(typeof(ArgumentException), () =>
            {
                var g = GrainClient.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), "Foo");
            });
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Derived_NoPrefix()
        {
            var g = GrainClient.GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId());
            Assert.False(g.Foo().Result);
            Assert.True(g.Bar().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Derived_WithFullName()
        {
            var grainFullName = typeof(DerivedFromBaseGrain).FullName;
            var g = GrainClient.GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId(), grainFullName);
            Assert.False(g.Foo().Result);
            Assert.True(g.Bar().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Derived_WithFullName_FromBase()
        {
            var grainFullName = typeof(DerivedFromBaseGrain).FullName;
            var g = GrainClient.GrainFactory.GetGrain<IBase>(GetRandomGrainId(), grainFullName);
            Assert.False(g.Foo().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Derived_WithPrefix()
        {
            var g = GrainClient.GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId(), "UnitTests.Grains");
            Assert.False(g.Foo().Result);
            Assert.True(g.Bar().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_Derived_WithWrongPrefix()
        {
            Xunit.Assert.Throws(typeof(ArgumentException), () =>
            {
                var g = GrainClient.GrainFactory.GetGrain<IDerivedFromBase>(GetRandomGrainId(), "Foo");
            });
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_OneImplementation_NoPrefix()
        {
            var g = GrainClient.GrainFactory.GetGrain<IBase1>(GetRandomGrainId());
            Assert.False(g.Foo().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_OneImplementation_Prefix()
        {
            var grainFullName = typeof(BaseGrain1).FullName;
            var g = GrainClient.GrainFactory.GetGrain<IBase1>(GetRandomGrainId(), grainFullName);
            Assert.False(g.Foo().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGrain_MultipleUnrelatedInterfaces()
        {
            var g1 = GrainClient.GrainFactory.GetGrain<IBase3>(GetRandomGrainId());
            Assert.False(g1.Foo().Result);
            var g2 = GrainClient.GrainFactory.GetGrain<IBase2>(GetRandomGrainId());
            Assert.True(g2.Bar().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetStringGrain()
        {
            var g = GrainClient.GrainFactory.GetGrain<IStringGrain>(Guid.NewGuid().ToString());
            Assert.True(g.Foo().Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Factory"), TestCategory("GetGrain")]
        public void GetGuidGrain()
        {
            var g = GrainClient.GrainFactory.GetGrain<IGuidGrain>(Guid.NewGuid());
            Assert.True(g.Foo().Result);
        }
    }
}
