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
        ExcludeOne,
        RequestContextBased,
    }

    [Serializable]
    public class TestCustomPlacementStrategy : PlacementStrategy
    {
        public CustomPlacementScenario Scenario { get; private set; }

        public static TestCustomPlacementStrategy FixedSilo { get; } = new TestCustomPlacementStrategy(CustomPlacementScenario.FixedSilo);
        public static TestCustomPlacementStrategy ExcludeOne { get; } = new TestCustomPlacementStrategy(CustomPlacementScenario.ExcludeOne);
        public static TestCustomPlacementStrategy RequestContextBased { get; } = new TestCustomPlacementStrategy(CustomPlacementScenario.RequestContextBased);

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
            base(GetCustomPlacementStrategy(scenario))
        {
            Scenario = scenario;
        }

        private static TestCustomPlacementStrategy GetCustomPlacementStrategy(CustomPlacementScenario scenario)
        {
            switch (scenario)
            {
                case CustomPlacementScenario.FixedSilo:
                    return TestCustomPlacementStrategy.FixedSilo;
                case CustomPlacementScenario.ExcludeOne:
                    return TestCustomPlacementStrategy.ExcludeOne;
                case CustomPlacementScenario.RequestContextBased:
                    return TestCustomPlacementStrategy.RequestContextBased;
                default:
                    throw new Exception("Unknown CustomPlacementScenario");
            }
        }
    }
}
