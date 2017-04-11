using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Orleans;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace UnitTests.GrainInterfaces
{
	public interface ICustomPlacementTestGrain : IGrainWithGuidKey
	{
		Task<string> GetRuntimeInstanceId();
	}


	public enum CustomePlacementScenario
	{
		FixedSilo,
		ExcludeOne
	}

	[Serializable]
	public class TestCustomPlacementStrategy : PlacementStrategy
	{
		public CustomePlacementScenario Scenario { get; private set; }

		public static TestCustomPlacementStrategy FixedSilo { get; } = new TestCustomPlacementStrategy(CustomePlacementScenario.FixedSilo);
		public static TestCustomPlacementStrategy ExcludeOne { get; } = new TestCustomPlacementStrategy(CustomePlacementScenario.ExcludeOne);

		internal TestCustomPlacementStrategy(CustomePlacementScenario scenario)
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
		public CustomePlacementScenario Scenario { get; private set; }

		public TestPlacementStrategyAttribute(CustomePlacementScenario scenario) :
			base(scenario == CustomePlacementScenario.FixedSilo ? TestCustomPlacementStrategy.FixedSilo : TestCustomPlacementStrategy.ExcludeOne)
		{
			Scenario = scenario;
		}
	}

	public class TestPlacementStrategyFixedSiloDirector : IPlacementDirector<TestCustomPlacementStrategy>
	{

		public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
		{
			var silos = context.GetCompatibleSilos(target).OrderBy(s => s).ToArray();
			var oddTick = DateTime.UtcNow.Ticks % 2 == 1;

			switch (((TestCustomPlacementStrategy)strategy).Scenario)
			{
				case CustomePlacementScenario.FixedSilo:
					return Task.FromResult(silos[silos.Length - 2]); // second from last silos.

				case CustomePlacementScenario.ExcludeOne:
					return Task.FromResult(oddTick ? silos[0] : silos[silos.Length - 1]); // randomly return first or last silos

				default:
					throw new InvalidOperationException(); // should never get here, only to make compiler happy
			}

		}
	}
}
