using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;
using GrainInterfaceUtils = Orleans.CodeGeneration.GrainInterfaceUtils;

namespace UnitTests.General
{
    #region simple interfaces

    public interface ITestGrain_VoidMethod : IAddressable
    {
        void VoidMethod();
    }

    public interface ITestGrain_IntMethod : IAddressable
    {
        int IntMethod();
    }

    public interface ITestGrain_IntProperty : IAddressable
    {
        int IntProperty { get; }
    }

    public interface ITestGrain_PropertySetter : IAddressable
    {
        Task<int> IntProperty { get; set; }
    }

    public interface ITestObserver_NonVoidMethod : IGrainObserver
    {
        Task NonVoidMethod();
    }

    public interface ITestObserver_Property : IGrainObserver
    {
        Task<int> IntProperty { get; }
    }

    public interface ITestGrain_OutArgument : IAddressable
    {
        Task Method(out int parameter);
    }

    public interface ITestGrain_RefArgument : IAddressable
    {
        Task Method(ref int parameter);
    }

    #endregion

    #region inheritance

    public interface IBaseGrain : IAddressable
    {
    }

    public interface IBaseObserver : IGrainObserver
    {
    }

    public interface IInheritedGrain_ObserverGrain_VoidMethod : IBaseGrain, IBaseObserver
    {
        void VoidMethod();
    }

    public interface IInheritedGrain_ObserverGrain_IntMethod : IBaseGrain, IBaseObserver
    {
        int IntMethod();
    }

    public interface IInheritedGrain_ObserverGrain_IntProperty : IBaseGrain, IBaseObserver
    {
        int IntProperty { get; }
    }

    public interface IInheritedGrain_ObserverGrain_PropertySetter : IBaseGrain, IBaseObserver
    {
        Task<int> IntProperty { get; set; }
    }

    public interface IBaseTaskGrain : IAddressable
    {
        Task VoidMethod();
    }

    public interface IBasePromiseGrain : IAddressable
    {
        Task VoidMethod();
    }

    public interface IDerivedTaskGrain : IBaseTaskGrain
    {
    }

    public interface IDerivedPromiseGrain : IBasePromiseGrain
    {
    }

    public interface IDerivedTaskGrainWithGrainRef : IBaseTaskGrain
    {
        Task<IBasePromiseGrain> GetGrain();
    }

    public interface IDerivedPromiseGrainWithGrainRef : IBasePromiseGrain
    {
        Task<IBaseTaskGrain> GetGrain();
    }

    #endregion

    /// <summary>
    /// Summary description for InterfaceRules
    /// </summary>
    public class InterfaceRulesTests
    {
        private readonly ITestOutputHelper output;

        public InterfaceRulesTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("BVT"), TestCategory("JSON")]
        public void JSON_JsonEchoGrain_IsConcreteGrainClass()
        {
            Type grainClass = typeof(JsonEchoGrain);

            IEnumerable<string> complaints;
            bool isConcreteGrainClass = TypeUtils.IsConcreteGrainClass(grainClass, out complaints);
            if (complaints != null)
            {
                foreach (string problem in complaints)
                {
                    output.WriteLine(problem);
                }
            }

            Type grainMarker = typeof(IGrain);
            Type grainBase = typeof(Grain);
            Assert.True(grainMarker.IsAssignableFrom(grainClass), $"{grainClass} is {grainMarker}");
            Assert.True(grainBase.IsAssignableFrom(grainClass), $"{grainClass} is {grainBase}");
            Assert.True(grainBase.GetTypeInfo().IsAssignableFrom(grainClass), $"{grainClass} is {grainBase}");

            Assert.True(isConcreteGrainClass, $"IsConcreteGrainClass {grainClass}");
        }

        #region simple interfaces

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public void InterfaceRules_VoidMethod()
        {
            Assert.Throws<GrainInterfaceUtils.RulesViolationException>(() =>
            GrainInterfaceUtils.ValidateInterface(typeof(ITestGrain_VoidMethod)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public void InterfaceRules_IntMethod()
        {
            Assert.Throws<GrainInterfaceUtils.RulesViolationException>(() =>
            GrainInterfaceUtils.ValidateInterface(typeof(ITestGrain_IntMethod)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public void InterfaceRules_IntProperty()
        {
            Assert.Throws<GrainInterfaceUtils.RulesViolationException>(() =>
            GrainInterfaceUtils.ValidateInterface(typeof(ITestGrain_IntProperty)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public void InterfaceRules_PropertySetter()
        {
            Assert.Throws<GrainInterfaceUtils.RulesViolationException>(() =>
            GrainInterfaceUtils.ValidateInterface(typeof(ITestGrain_PropertySetter)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public void InterfaceRules_Observer_NonVoidMethod()
        {
            Assert.Throws<GrainInterfaceUtils.RulesViolationException>(() =>
            GrainInterfaceUtils.ValidateInterface(typeof(ITestObserver_NonVoidMethod)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public void InterfaceRules_Observer_Property()
        {
            Assert.Throws<GrainInterfaceUtils.RulesViolationException>(() =>
            GrainInterfaceUtils.ValidateInterface(typeof(ITestObserver_Property)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public void InterfaceRules_OutArgument()
        {
            Assert.Throws<GrainInterfaceUtils.RulesViolationException>(() =>
            GrainInterfaceUtils.ValidateInterface(typeof(ITestGrain_OutArgument)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public void InterfaceRules_RefArgument()
        {
            Assert.Throws<GrainInterfaceUtils.RulesViolationException>(() =>
            GrainInterfaceUtils.ValidateInterface(typeof(ITestGrain_RefArgument)));
        }

        #endregion

        #region inheritence

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public void InterfaceRules_ObserverGrain_VoidMethod()
        {
            Assert.Throws<GrainInterfaceUtils.RulesViolationException>(() =>
            GrainInterfaceUtils.ValidateInterface(typeof(IInheritedGrain_ObserverGrain_VoidMethod)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public void InterfaceRules_ObserverGrain_IntMethod()
        {
            Assert.Throws<GrainInterfaceUtils.RulesViolationException>(() =>
            GrainInterfaceUtils.ValidateInterface(typeof(IInheritedGrain_ObserverGrain_IntMethod)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public void InterfaceRules_ObserverGrain_IntProperty()
        {
            Assert.Throws<GrainInterfaceUtils.RulesViolationException>(() =>
            GrainInterfaceUtils.ValidateInterface(typeof(IInheritedGrain_ObserverGrain_IntProperty)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public void InterfaceRules_ObserverGrain_PropertySetter()
        {
            Assert.Throws<GrainInterfaceUtils.RulesViolationException>(() =>
            GrainInterfaceUtils.ValidateInterface(typeof(IInheritedGrain_ObserverGrain_PropertySetter)));
        }

        #endregion
    }
}
