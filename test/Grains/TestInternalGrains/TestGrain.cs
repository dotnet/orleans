using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class TestGrain : Grain, ITestGrain
    {
        private readonly string _id = Guid.NewGuid().ToString();
        private string label;
        private ILogger logger;
        private IDisposable timer;

        public TestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            if (this.GetPrimaryKeyLong() == -2)
                throw new ArgumentException("Primary key cannot be -2 for this test case");

            label = this.GetPrimaryKeyLong().ToString();
            logger.LogInformation("OnActivateAsync");

            return base.OnActivateAsync(cancellationToken);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.LogInformation("!!! OnDeactivateAsync");
            return base.OnDeactivateAsync(reason, cancellationToken);
        }

        public Task<long> GetKey()
        {
            return Task.FromResult(this.GetPrimaryKeyLong());
        }

        public Task<string> GetLabel()
        {
            return Task.FromResult(label);
        }

        public async Task DoLongAction(TimeSpan timespan, string str)
        {
            logger.LogInformation("DoLongAction {String} received", str);
            await Task.Delay(timespan);
        }

        public Task SetLabel(string label)
        {
            this.label = label;
            logger.LogInformation("SetLabel {Label} received", label);
            return Task.CompletedTask;
        }

        public Task StartTimer()
        {
            logger.LogInformation("StartTimer.");
            timer = base.RegisterTimer(TimerTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));

            return Task.CompletedTask;
        }

        private Task TimerTick(object data)
        {
            logger.LogInformation("TimerTick.");
            return Task.CompletedTask;
        }

        public async Task<Tuple<string, string>> TestRequestContext()
        {
            string bar1 = null;
            RequestContext.Set("jarjar", "binks");

            var task = Task.Factory.StartNew(() =>
            {
                bar1 = (string) RequestContext.Get("jarjar");
                logger.LogInformation("bar = {Bar}.", bar1);
            });

            string bar2 = null;
            var ac = Task.Factory.StartNew(() =>
            {
                bar2 = (string) RequestContext.Get("jarjar");
                logger.LogInformation("bar = {Bar}.", bar2);
            });

            await Task.WhenAll(task, ac);
            return new Tuple<string, string>(bar1, bar2);
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(RuntimeIdentity);
        }

        public Task<string> GetActivationId()
        {
            return Task.FromResult(_id);
        }

        public Task<ITestGrain> GetGrainReference()
        {
            return Task.FromResult(this.AsReference<ITestGrain>());
        }

        public Task<IGrain[]> GetMultipleGrainInterfaces_Array()
        {
            var grains = new IGrain[5];
            for (var i = 0; i < grains.Length; i++)
            {
                grains[i] = GrainFactory.GetGrain<ITestGrain>(i);
            }
            return Task.FromResult(grains);
        }

        public Task<List<IGrain>> GetMultipleGrainInterfaces_List()
        {
            var grains = new IGrain[5];
            for (var i = 0; i < grains.Length; i++)
            {
                grains[i] = GrainFactory.GetGrain<ITestGrain>(i);
            }
            return Task.FromResult(grains.ToList());
        }
    }

    public class TestGrainLongActivateAsync : Grain, ITestGrainLongOnActivateAsync
    {
        public TestGrainLongActivateAsync()
        {
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));

            if (this.GetPrimaryKeyLong() == -2)
                throw new ArgumentException("Primary key cannot be -2 for this test case");

            await base.OnActivateAsync(cancellationToken);
        }

        public Task<long> GetKey()
        {
            return Task.FromResult(this.GetPrimaryKeyLong());
        }
    }

    internal class GuidTestGrain : Grain, IGuidTestGrain
    {
        private readonly string _id = Guid.NewGuid().ToString();

        private string label;
        private ILogger logger;

        public GuidTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            //if (this.GetPrimaryKeyLong() == -2)
            //    throw new ArgumentException("Primary key cannot be -2 for this test case");

            label = this.GetPrimaryKey().ToString();
            logger.LogInformation("OnActivateAsync");

            return Task.CompletedTask;
        }

        public Task<Guid> GetKey()
        {
            return Task.FromResult(this.GetPrimaryKey());
        }

        public Task<string> GetLabel()
        {
            return Task.FromResult(label);
        }

        public Task SetLabel(string label)
        {
            this.label = label;
            return Task.CompletedTask;
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(RuntimeIdentity);
        }

        public Task<string> GetActivationId()
        {
            return Task.FromResult(_id);
        }
    }

    internal class OneWayGrain : Grain, IOneWayGrain, ISimpleGrainObserver
    {
        private readonly string _id = Guid.NewGuid().ToString();
        private int count;
        private TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IOneWayGrain other;
        private GrainLocator grainLocator;
        private int _numSignals;

        public OneWayGrain(GrainLocator grainLocator) => this.grainLocator = grainLocator;

        private ILocalGrainDirectory LocalGrainDirectory => this.ServiceProvider.GetRequiredService<ILocalGrainDirectory>();
        private ILocalSiloDetails LocalSiloDetails => this.ServiceProvider.GetRequiredService<ILocalSiloDetails>();

        public Task Notify()
        {
            this.count++;
            return Task.CompletedTask;
        }

        public Task Notify(ISimpleGrainObserver observer)
        {
            this.count++;
            observer.StateChanged(this.count - 1, this.count);
            return Task.CompletedTask;
        }

        public ValueTask NotifyValueTask(ISimpleGrainObserver observer)
        {
            this.count++;
            observer.StateChanged(this.count - 1, this.count);
            return default;
        }

        public async Task<bool> NotifyOtherGrain(IOneWayGrain otherGrain, ISimpleGrainObserver observer)
        {
            var task = otherGrain.Notify(observer);
            var completedSynchronously = task.Status == TaskStatus.RanToCompletion;
            await task;
            return completedSynchronously;
        }

        public async Task<bool> NotifyOtherGrainValueTask(IOneWayGrain otherGrain, ISimpleGrainObserver observer)
        {
            var task = otherGrain.NotifyValueTask(observer);
            var completedSynchronously = task.IsCompleted;
            await task;
            return completedSynchronously;
        }

        public async Task<IOneWayGrain> GetOtherGrain()
        {
            return this.other ?? (this.other = await GetGrainOnOtherSilo());

            async Task<IOneWayGrain> GetGrainOnOtherSilo()
            {
                while (true)
                {
                    var candidate = this.GrainFactory.GetGrain<IOneWayGrain>(Guid.NewGuid());
                    var directorySilo = await candidate.GetPrimaryForGrain();
                    var thisSilo = await this.GetSiloAddress();
                    var candidateSilo = await candidate.GetSiloAddress();
                    if (!directorySilo.Equals(candidateSilo)
                        && !directorySilo.Equals(thisSilo)
                        && !candidateSilo.Equals(thisSilo))
                    {
                        return candidate;
                    }
                }
            }
        }

        public Task<string> GetActivationId()
        {
            return Task.FromResult(_id);
        }

        public Task<string> GetActivationAddress(IGrain grain)
        {
            var grainId = ((GrainReference)grain).GrainId;
            if (this.grainLocator.TryLookupInCache(grainId, out var result))
            {
                return Task.FromResult(result.ToString());
            }

            return Task.FromResult<string>(null);
        }

        public Task NotifyOtherGrain() => this.other.Notify(this.AsReference<ISimpleGrainObserver>());

        public Task<int> GetCount() => Task.FromResult(this.count);

        public Task Deactivate()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public Task ThrowsOneWay()
        {
            throw new Exception("GET OUT!");
        }

        public ValueTask ThrowsOneWayValueTask()
        {
            throw new Exception("GET OUT (ValueTask)!");
        }

        public Task<SiloAddress> GetSiloAddress()
        {
            return Task.FromResult(this.LocalSiloDetails.SiloAddress);
        }

        public Task<SiloAddress> GetPrimaryForGrain()
        {
            var grainId = (GrainId)this.GrainId;
            var primaryForGrain = this.LocalGrainDirectory.GetPrimaryForGrain(grainId);
            return Task.FromResult(primaryForGrain);
        }

        public void StateChanged(int a, int b)
        {
            _numSignals++;
            this.tcs.TrySetResult(null);
        }

        public async Task SendSignalTo(IOneWayGrain grain)
        {
            await grain.Signal(_id);
        }

        public Task SignalSelfViaOther() => this.other.SendSignalTo(this.AsReference<IOneWayGrain>());

        public async Task<(int NumSignals, string SignallerId)> WaitForSignal()
        {
            var signallerId = await this.tcs.Task;
            this.tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return (_numSignals, signallerId);
        }

        public Task Signal(string id)
        {
            _numSignals++;
            tcs.TrySetResult(id);
            return Task.CompletedTask;
        }
    }

    public class CanBeOneWayGrain : Grain, ICanBeOneWayGrain
    {
        private int count;

        public Task Notify()
        {
            this.count++;
            return Task.CompletedTask;
        }

        public Task Notify(ISimpleGrainObserver observer)
        {
            this.count++;
            observer.StateChanged(this.count - 1, this.count);
            return Task.CompletedTask;
        }

        public ValueTask NotifyValueTask(ISimpleGrainObserver observer)
        {
            this.count++;
            observer.StateChanged(this.count - 1, this.count);
            return default;
        }

        public Task<int> GetCount() => Task.FromResult(this.count);

        public Task Throws()
        {
            throw new Exception("GET OUT!");
        }

        public ValueTask ThrowsValueTask()
        {
            throw new Exception("GET OUT!");
        }
    }
}
