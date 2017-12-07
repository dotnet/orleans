using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Orleans;
using Orleans.Placement;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface ICustomPlacementTestGrain : IGrainWithGuidKey
    {
        Task<string> GetRuntimeInstanceId();
    }

    public interface IHashBasedPlacementGrain : IGrainWithGuidKey
    {
        Task<SiloAddress> GetSiloAddress();
    }


    public enum CustomPlacementScenario
    {
        FixedSilo,
        ExcludeOne
    }

    [Serializable]
    public class TestCustomPlacementStrategy : PlacementStrategy
    {
        public CustomPlacementScenario Scenario { get; private set; }

        public static TestCustomPlacementStrategy FixedSilo { get; } = new TestCustomPlacementStrategy(CustomPlacementScenario.FixedSilo);
        public static TestCustomPlacementStrategy ExcludeOne { get; } = new TestCustomPlacementStrategy(CustomPlacementScenario.ExcludeOne);

        internal TestCustomPlacementStrategy(CustomPlacementScenario scenario)
        {
            Scenario = scenario;
        }

        public override bool Equals(object obj)
        {
            return obj is TestCustomPlacementStrategy && Scenario == ((TestCustomPlacementStrategy)obj).Scenario;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode() ^ Scenario.GetHashCode();
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class TestPlacementStrategyAttribute : PlacementAttribute
    {
        public CustomPlacementScenario Scenario { get; private set; }

        public TestPlacementStrategyAttribute(CustomPlacementScenario scenario) :
            base(scenario == CustomPlacementScenario.FixedSilo ? TestCustomPlacementStrategy.FixedSilo : TestCustomPlacementStrategy.ExcludeOne)
        {
            Scenario = scenario;
        }
    }
}
