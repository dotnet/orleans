using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Grains
{
    internal class SimpleActivateDeactivateTestGrain : Grain, ISimpleActivateDeactivateTestGrain
    {
        private ILogger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private bool doingActivate;
        private bool doingDeactivate;

        public SimpleActivateDeactivateTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.Info("OnActivateAsync");
            watcher = GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Not doing Deactivate yet");
            doingActivate = true;
            await watcher.RecordActivateCall(RuntimeHelpers.GetHashCode(this).ToString("X"));
            Assert.True(doingActivate, "Activate method still running");
            doingActivate = false;
        }

        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.Info("OnDeactivateAsync");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Not doing Deactivate yet");
            doingDeactivate = true;
            await watcher.RecordDeactivateCall(RuntimeHelpers.GetHashCode(this).ToString("X"));
            Assert.True(doingDeactivate, "Deactivate method still running");
            doingDeactivate = false;
        }

        public Task<string> DoSomething()
        {
            logger.Info("DoSomething");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Deactivate method should not be running yet");
            return Task.FromResult(RuntimeHelpers.GetHashCode(this).ToString("X"));
        }

        public Task DoDeactivate()
        {
            logger.Info("DoDeactivate");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Deactivate method should not be running yet");
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }

    internal class TailCallActivateDeactivateTestGrain : Grain, ITailCallActivateDeactivateTestGrain
    {
        private ILogger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private bool doingActivate;
        private bool doingDeactivate;

        public TailCallActivateDeactivateTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.Info("OnActivateAsync");
            watcher = GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Not doing Deactivate yet");
            doingActivate = true;
            return watcher.RecordActivateCall(RuntimeHelpers.GetHashCode(this).ToString("X"))
                .ContinueWith((Task t) =>
                {
                    Assert.False(t.IsFaulted, "RecordActivateCall failed");
                    Assert.True(doingActivate, "Doing Activate");
                    doingActivate = false;
                });
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.Info("OnDeactivateAsync");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Not doing Deactivate yet");
            doingDeactivate = true;
            return watcher.RecordDeactivateCall(RuntimeHelpers.GetHashCode(this).ToString("X"))
                .ContinueWith((Task t) =>
                {
                    Assert.False(t.IsFaulted, "RecordDeactivateCall failed");
                    Assert.True(doingDeactivate, "Doing Deactivate");
                    doingDeactivate = false;
                });
        }

        public Task<string> DoSomething()
        {
            logger.Info("DoSomething");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Deactivate method should not be running yet");
            return Task.FromResult(RuntimeHelpers.GetHashCode(this).ToString("X"));
        }

        public Task DoDeactivate()
        {
            logger.Info("DoDeactivate");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Deactivate method should not be running yet");
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }

    internal class LongRunningActivateDeactivateTestGrain : Grain, ILongRunningActivateDeactivateTestGrain
    {
        private ILogger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private bool doingActivate;
        private bool doingDeactivate;

        public LongRunningActivateDeactivateTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            watcher = GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);

            Assert.False(doingActivate, "Not doing Activate yet");
            Assert.False(doingDeactivate, "Not doing Deactivate yet");
            doingActivate = true;

            logger.Info("OnActivateAsync");

            // Spawn Task to run on default .NET thread pool
            var task = Task.Factory.StartNew(() =>
            {
                logger.Info("Started-OnActivateAsync-SubTask");
                Assert.True(TaskScheduler.Current == TaskScheduler.Default,
                    "Running under default .NET Task scheduler");
                Assert.True(doingActivate, "Still doing Activate in Sub-Task");
                logger.Info("Finished-OnActivateAsync-SubTask");
            }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            await task;

            logger.Info("Started-OnActivateAsync");

            await watcher.RecordActivateCall(RuntimeHelpers.GetHashCode(this).ToString("X"));
            Assert.True(doingActivate, "Doing Activate");

            logger.Info("OnActivateAsync-Sleep");
            Thread.Sleep(TimeSpan.FromSeconds(1));
            Assert.True(doingActivate, "Still doing Activate after Sleep");

            logger.Info("Finished-OnActivateAsync");
            doingActivate = false;
        }

        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.Info("OnDeactivateAsync");

            Assert.False(doingActivate, "Not doing Activate yet");
            Assert.False(doingDeactivate, "Not doing Deactivate yet");
            doingDeactivate = true;

            logger.Info("Started-OnDeactivateAsync");

            await watcher.RecordDeactivateCall(RuntimeHelpers.GetHashCode(this).ToString("X"));
            Assert.True(doingDeactivate, "Doing Deactivate");

            logger.Info("OnDeactivateAsync-Sleep");
            Thread.Sleep(TimeSpan.FromSeconds(1));
            logger.Info("Finished-OnDeactivateAsync");
            doingDeactivate = false;
        }

        public Task<string> DoSomething()
        {
            logger.Info("DoSomething");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Deactivate method should not be running yet");
            return Task.FromResult(RuntimeHelpers.GetHashCode(this).ToString("X"));
        }

        public Task DoDeactivate()
        {
            logger.Info("DoDeactivate");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Deactivate method should not be running yet");
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }

    internal class TaskActionActivateDeactivateTestGrain : Grain, ITaskActionActivateDeactivateTestGrain
    {
        private ILogger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private bool doingActivate;
        private bool doingDeactivate;

        public TaskActionActivateDeactivateTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            Assert.NotNull(TaskScheduler.Current);
            Assert.NotEqual(TaskScheduler.Current, TaskScheduler.Default);
            var startMe =
                new Task(
                    () =>
                    {
                        Assert.NotNull(TaskScheduler.Current);
                        Assert.NotEqual(TaskScheduler.Current, TaskScheduler.Default);
                        logger.Info("OnActivateAsync");

                        watcher = GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);

                        Assert.False(doingActivate, "Not doing Activate");
                        Assert.False(doingDeactivate, "Not doing Deactivate");
                        doingActivate = true;
                    });
            // we want to use Task.ContinueWith with an async lambda, an explicitly typed variable is required to avoid
            // writing code that doesn't do what i think it is doing.
            Func<Task> asyncCont =
                async () =>
                {
                    Assert.NotNull(TaskScheduler.Current);
                    Assert.NotEqual(TaskScheduler.Current, TaskScheduler.Default);
                    logger.Info("Started-OnActivateAsync");

                    Assert.True(doingActivate, "Doing Activate 1");
                    Assert.False(doingDeactivate, "Not doing Deactivate");

                    try
                    {
                        logger.Info("Calling RecordActivateCall");
                        await watcher.RecordActivateCall(RuntimeHelpers.GetHashCode(this).ToString("X"));
                        logger.Info("Returned from calling RecordActivateCall");
                    }
                    catch (Exception exc)
                    {
                        var msg = "RecordActivateCall failed with error " + exc;
                        logger.Error(0, msg);
                        Assert.True(false, msg);
                    }

                    Assert.True(doingActivate, "Doing Activate 2");
                    Assert.False(doingDeactivate, "Not doing Deactivate");

                    await Task.Delay(TimeSpan.FromSeconds(1));

                    doingActivate = false;

                    logger.Info("Finished-OnActivateAsync");
                };
            var awaitMe = startMe.ContinueWith(_ => asyncCont()).Unwrap();
            startMe.Start();
            await awaitMe;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            Task.Factory.StartNew(() => logger.Info("OnDeactivateAsync"));

            Assert.False(doingActivate, "Not doing Activate");
            Assert.False(doingDeactivate, "Not doing Deactivate");
            doingDeactivate = true;

            logger.Info("Started-OnDeactivateAsync");
            return watcher.RecordDeactivateCall(RuntimeHelpers.GetHashCode(this).ToString("X"))
                .ContinueWith((Task t) =>
                {
                    Assert.False(t.IsFaulted, "RecordDeactivateCall failed");
                    Assert.True(doingDeactivate, "Doing Deactivate");
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                    doingDeactivate = false;
                })
                .ContinueWith((Task t) => logger.Info("Finished-OnDeactivateAsync"),
                    TaskContinuationOptions.ExecuteSynchronously);
        }

        public Task<string> DoSomething()
        {
            logger.Info("DoSomething");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Deactivate method should not be running yet");
            return Task.FromResult(RuntimeHelpers.GetHashCode(this).ToString("X"));
        }

        public Task DoDeactivate()
        {
            logger.Info("DoDeactivate");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Deactivate method should not be running yet");
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }

    public class BadActivateDeactivateTestGrain : Grain, IBadActivateDeactivateTestGrain
    {
        private ILogger logger;

        public BadActivateDeactivateTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.Info("OnActivateAsync");
            throw new ApplicationException("Thrown from Application-OnActivateAsync");
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.Info("OnDeactivateAsync");
            throw new ApplicationException("Thrown from Application-OnDeactivateAsync");
        }

        public Task ThrowSomething()
        {
            logger.Info("ThrowSomething");
            throw new InvalidOperationException("Exception should have been thrown from Activate");
        }

        public Task<long> GetKey()
        {
            logger.Info("GetKey");
            //return this.GetPrimaryKeyLong();
            throw new InvalidOperationException("Exception should have been thrown from Activate");
        }
    }

    internal class BadConstructorTestGrain : Grain, IBadConstructorTestGrain
    {
        public BadConstructorTestGrain()
        {
            throw new ApplicationException("Thrown from Constructor");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException("OnActivateAsync should not have been called");
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("OnDeactivateAsync(...) should not have been called");
        }

        public Task<string> DoSomething()
        {
            throw new NotImplementedException("DoSomething should not have been called");
        }
    }

    internal class DeactivatingWhileActivatingTestGrain : Grain, IDeactivatingWhileActivatingTestGrain
    {
        private ILogger logger;

        public DeactivatingWhileActivatingTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.Info("OnActivateAsync");
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.Info("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        public Task<string> DoSomething()
        {
            logger.Info("DoSomething");
            throw new NotImplementedException("DoSomething should not have been called");
        }
    }

    internal class CreateGrainReferenceTestGrain : Grain, ICreateGrainReferenceTestGrain
    {
        private ILogger logger;

        //private IEchoGrain orleansManagedGrain;
        private ITestGrain grain;

        public CreateGrainReferenceTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            grain = GrainFactory.GetGrain<ITestGrain>(1);
            logger.Info("OnActivateAsync");
            grain = GrainFactory.GetGrain<ITestGrain>(1);
            return Task.CompletedTask;
        }

        public async Task<string> DoSomething()
        {
            logger.Info("DoSomething");
            var guid = Guid.NewGuid();
            await grain.SetLabel(guid.ToString());
            var label = await grain.GetLabel();

            if (string.IsNullOrEmpty(label))
            {
                throw new ArgumentException("Bad data: Null label returned");
            }
            return RuntimeHelpers.GetHashCode(this).ToString("X");
        }

        public async Task ForwardCall(IBadActivateDeactivateTestGrain otherGrain)
        {
            logger.Info("ForwardCall to " + otherGrain);
            await otherGrain.ThrowSomething();
        }
    }
}
