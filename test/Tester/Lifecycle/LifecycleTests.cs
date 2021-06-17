using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
            Dictionary<TestStages, List<Observer>> observersByStage = await RunLifecycle(observerCountByStage, null, null);

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
                Dictionary<TestStages, List<Observer>> observersByStage = await RunLifecycle(observerCountByStage, stage, null);

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
                Dictionary<TestStages, List<Observer>> observersByStage = await RunLifecycle(observerCountByStage, null, stage);

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

        [Fact, TestCategory("BVT"), TestCategory("Lifecycle")]
        public async Task MultiStageObserverLifecycleTest()
        {
            var lifecycle = new LifecycleSubject(null);
            var multiStageObserver = new MultiStageObserver();
            multiStageObserver.Participate(lifecycle);
            await lifecycle.OnStart();
            await lifecycle.OnStop();
            Assert.Equal(4, multiStageObserver.Started.Count);
            Assert.Equal(4, multiStageObserver.Stopped.Count);
            Assert.True(multiStageObserver.Started.Values.All(o => o));
            Assert.True(multiStageObserver.Stopped.Values.All(o => o));
        }

        private async Task<Dictionary<TestStages,List<Observer>>> RunLifecycle(Dictionary<TestStages,int> observerCountByStage, TestStages? failOnStart, TestStages? failOnStop)
        {
            // setup lifecycle observers
            var observersByStage = new Dictionary<TestStages, List<Observer>>();
            var lifecycle = new LifecycleSubject(null);
            foreach (KeyValuePair<TestStages, int> kvp in observerCountByStage)
            {
                List<Observer> observers = Enumerable
                    .Range(0, kvp.Value)
                    .Select(i => new Observer(failOnStart.HasValue && kvp.Key == failOnStart, failOnStop.HasValue && kvp.Key == failOnStop))
                    .ToList();
                observersByStage[kvp.Key] = observers;
                observers.ForEach(o => lifecycle.Subscribe((int)kvp.Key, o));
            }

            // run lifecycle
            if (failOnStart.HasValue)
            {
                await Assert.ThrowsAsync<Exception>(() => lifecycle.OnStart());
            }
            else
            {
                await lifecycle.OnStart();
            }
            await lifecycle.OnStop();

            // return results
            return observersByStage;
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

            public Task OnStart(CancellationToken ct)
            {
                this.Started = true;
                this.FailedOnStart = this.failOnStart;
                if (this.failOnStart) throw new Exception("failOnStart");
                return Task.CompletedTask;
            }

            public Task OnStop(CancellationToken ct)
            {
                this.Stopped = true;
                this.FailedOnStop = this.failOnStop;
                if (this.failOnStop) throw new Exception("failOnStop");
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Single component which takes action at multiple stages of the lifecycle (most common expected pattern)
        /// </summary>
        private class MultiStageObserver : ILifecycleParticipant<ILifecycleObservable>
        {
            public Dictionary<TestStages,bool> Started { get; } = new Dictionary<TestStages, bool>(); 
            public Dictionary<TestStages, bool> Stopped { get; } = new Dictionary<TestStages, bool>();


            private Task OnStartStage(TestStages stage)
            {
                this.Started[stage] = true;
                return Task.CompletedTask;
            }

            private Task OnStopStage(TestStages stage)
            {
                this.Stopped[stage] = true;
                return Task.CompletedTask;
            }

            public void Participate(ILifecycleObservable lifecycle)
            {
                lifecycle.Subscribe<MultiStageObserver>((int)TestStages.Down, ct => OnStartStage(TestStages.Down), ct => OnStopStage(TestStages.Down));
                lifecycle.Subscribe<MultiStageObserver>((int)TestStages.Initialize, ct => OnStartStage(TestStages.Initialize), ct => OnStopStage(TestStages.Initialize));
                lifecycle.Subscribe<MultiStageObserver>((int)TestStages.Configure, ct => OnStartStage(TestStages.Configure), ct => OnStopStage(TestStages.Configure));
                lifecycle.Subscribe<MultiStageObserver>((int)TestStages.Run, ct => OnStartStage(TestStages.Run), ct => OnStopStage(TestStages.Run));
            }
        }
    }
}
