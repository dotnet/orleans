
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Xunit;

namespace Tester
{
    public class LifecycleTests
    {
        [Fact, TestCategory("BVT"), TestCategory("Lifecycle")]
        public async Task FullLifecycleTest()
        {
            const int observersPerStage = 2;
            Dictionary<TestStages, int> observerCountByStage = new Dictionary<TestStages, int>();
            foreach (TestStages stage in Enum.GetValues(typeof(TestStages)))
            {
                observerCountByStage[stage] = observersPerStage;
            }
            Dictionary<TestStages, List<Observer>> observersByStage = await RunLifeCycle(observerCountByStage, null, null);

            Assert.Equal(observerCountByStage.Count, observersByStage.Count);
            foreach (KeyValuePair<TestStages,List<Observer>> kvp in observersByStage)
            {
                Assert.Equal(observerCountByStage[kvp.Key], kvp.Value.Count);
                Assert.True(kvp.Value.All(o => o.Started));
                Assert.True(kvp.Value.All(o => o.Stopped));
                Assert.True(kvp.Value.All(o => !o.FailedOnStart));
                Assert.True(kvp.Value.All(o => !o.FailedOnStop));
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Lifecycle")]
        public async Task FailOnStartOnEachStageLifecycleTest()
        {
            const int observersPerStage = 2;
            Dictionary<TestStages, int> observerCountByStage = new Dictionary<TestStages, int>();
            foreach (TestStages stage in Enum.GetValues(typeof(TestStages)))
            {
                observerCountByStage[stage] = observersPerStage;
            }

            foreach (TestStages stage in Enum.GetValues(typeof(TestStages)))
            {
                Dictionary<TestStages, List<Observer>> observersByStage = await RunLifeCycle(observerCountByStage, stage, null);

                Assert.Equal(observerCountByStage.Count, observersByStage.Count);
                foreach (KeyValuePair<TestStages, List<Observer>> kvp in observersByStage)
                {
                    Assert.Equal(observerCountByStage[kvp.Key], kvp.Value.Count);
                    if (kvp.Key < stage)
                    {
                        Assert.True(kvp.Value.All(o => o.Started));
                        Assert.True(kvp.Value.All(o => o.Stopped));
                        Assert.True(kvp.Value.All(o => !o.FailedOnStart));
                        Assert.True(kvp.Value.All(o => !o.FailedOnStop));
                    } else if (kvp.Key == stage)
                    {
                        Assert.True(kvp.Value.All(o => o.Started));
                        Assert.True(kvp.Value.All(o => o.Stopped));
                        Assert.True(kvp.Value.All(o => o.FailedOnStart));
                        Assert.True(kvp.Value.All(o => !o.FailedOnStop));
                    } else if (kvp.Key > stage)
                    {
                        Assert.True(kvp.Value.All(o => !o.Started));
                        Assert.True(kvp.Value.All(o => !o.Stopped));
                        Assert.True(kvp.Value.All(o => !o.FailedOnStart));
                        Assert.True(kvp.Value.All(o => !o.FailedOnStop));
                    }
                }
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Lifecycle")]
        public async Task FailOnStopOnEachStageLifecycleTest()
        {
            const int observersPerStage = 2;
            Dictionary<TestStages, int> observerCountByStage = new Dictionary<TestStages, int>();
            foreach (TestStages stage in Enum.GetValues(typeof(TestStages)))
            {
                observerCountByStage[stage] = observersPerStage;
            }

            foreach (TestStages stage in Enum.GetValues(typeof(TestStages)))
            {
                Dictionary<TestStages, List<Observer>> observersByStage = await RunLifeCycle(observerCountByStage, null, stage);

                Assert.Equal(observerCountByStage.Count, observersByStage.Count);
                foreach (KeyValuePair<TestStages, List<Observer>> kvp in observersByStage)
                {
                    Assert.Equal(observerCountByStage[kvp.Key], kvp.Value.Count);
                    if (kvp.Key != stage)
                    {
                        Assert.True(kvp.Value.All(o => o.Started));
                        Assert.True(kvp.Value.All(o => o.Stopped));
                        Assert.True(kvp.Value.All(o => !o.FailedOnStart));
                        Assert.True(kvp.Value.All(o => !o.FailedOnStop));
                    }
                    else if (kvp.Key == stage)
                    {
                        Assert.True(kvp.Value.All(o => o.Started));
                        Assert.True(kvp.Value.All(o => o.Stopped));
                        Assert.True(kvp.Value.All(o => !o.FailedOnStart));
                        Assert.True(kvp.Value.All(o => o.FailedOnStop));
                    }
                }
            }
        }

        private enum TestStages
        {
            Down,
            Initialize,
            Configure,
            Run,
        }

        private class Observer : ILifecycleObserver
        {
            private readonly bool failOnStart;
            private readonly bool failOnStop;

            public bool Started { get; private set; }
            public bool Stopped { get; private set; }
            public bool FailedOnStart { get; private set; }
            public bool FailedOnStop { get; private set; }

            public Observer(bool failOnStart, bool failOnStop)
            {
                this.failOnStart = failOnStart;
                this.failOnStop = failOnStop;
            }

            public Task OnStart()
            {
                this.Started = true;
                this.FailedOnStart = this.failOnStart;
                if(this.failOnStart) throw new Exception("failOnStart");
                return Task.CompletedTask;
            }

            public Task OnStop()
            {
                this.Stopped = true;
                this.FailedOnStop = this.failOnStop;
                if (this.failOnStop) throw new Exception("failOnStop");
                return Task.CompletedTask;
            }
        }
        private async Task<Dictionary<TestStages,List<Observer>>> RunLifeCycle(Dictionary<TestStages,int> observerCountByStage, TestStages? failOnStart, TestStages? failOnStop)
        {
            // setup lifecycle observers
            var observersByStage = new Dictionary<TestStages, List<Observer>>();
            var lifecycle = new LifecycleObservable<TestStages>(null);
            foreach (KeyValuePair<TestStages, int> kvp in observerCountByStage)
            {
                List<Observer> observers = Enumerable
                    .Range(0, kvp.Value)
                    .Select(i => new Observer(failOnStart.HasValue && kvp.Key == failOnStart, failOnStop.HasValue && kvp.Key == failOnStop))
                    .ToList();
                observersByStage[kvp.Key] = observers;
                observers.ForEach(o => lifecycle.Subscribe(kvp.Key, o));
            }

            // run lifecycle
            if (failOnStart.HasValue)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => lifecycle.OnStart());
            }
            else
            {
                await lifecycle.OnStart();
            }
            await lifecycle.OnStop();

            // return results
            return observersByStage;
        }
    }
}
