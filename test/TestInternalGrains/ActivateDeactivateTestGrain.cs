using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Grains
{
    internal class SimpleActivateDeactivateTestGrain : Grain, ISimpleActivateDeactivateTestGrain
    {
        private Logger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private bool doingActivate;
        private bool doingDeactivate;

        public override async Task OnActivateAsync()
        {
            logger = GetLogger();
            logger.Info("OnActivateAsync");
            watcher = GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Not doing Deactivate yet");
            doingActivate = true;
            await watcher.RecordActivateCall(Data.ActivationId.ToString());
            Assert.True(doingActivate, "Activate method still running");
            doingActivate = false;
        }

        public override async Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Not doing Deactivate yet");
            doingDeactivate = true;
            await watcher.RecordDeactivateCall(Data.ActivationId.ToString());
            Assert.True(doingDeactivate, "Deactivate method still running");
            doingDeactivate = false;
        }

        public Task<string> DoSomething()
        {
            logger.Info("DoSomething");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Deactivate method should not be running yet");
            return Task.FromResult(Data.ActivationId.ToString());
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
        private Logger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private bool doingActivate;
        private bool doingDeactivate;

        public override Task OnActivateAsync()
        {
            logger = GetLogger();
            logger.Info("OnActivateAsync");
            watcher = GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Not doing Deactivate yet");
            doingActivate = true;
            return watcher.RecordActivateCall(Data.ActivationId.ToString())
                .ContinueWith((Task t) =>
                {
                    Assert.False(t.IsFaulted, "RecordActivateCall failed");
                    Assert.True(doingActivate, "Doing Activate");
                    doingActivate = false;
                });
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            Assert.False(doingActivate, "Activate method should have finished");
            Assert.False(doingDeactivate, "Not doing Deactivate yet");
            doingDeactivate = true;
            return watcher.RecordDeactivateCall(Data.ActivationId.ToString())
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
            return Task.FromResult(Data.ActivationId.ToString());
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
        private Logger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private bool doingActivate;
        private bool doingDeactivate;

        public override async Task OnActivateAsync()
        {
            logger = GetLogger();
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

            await watcher.RecordActivateCall(Data.ActivationId.ToString());
            Assert.True(doingActivate, "Doing Activate");

            logger.Info("OnActivateAsync-Sleep");
            Thread.Sleep(TimeSpan.FromSeconds(1));
            Assert.True(doingActivate, "Still doing Activate after Sleep");

            logger.Info("Finished-OnActivateAsync");
            doingActivate = false;
        }

        public override async Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");

            Assert.False(doingActivate, "Not doing Activate yet");
            Assert.False(doingDeactivate, "Not doing Deactivate yet");
            doingDeactivate = true;

            logger.Info("Started-OnDeactivateAsync");

            await watcher.RecordDeactivateCall(Data.ActivationId.ToString());
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
            return Task.FromResult(Data.ActivationId.ToString());
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
        private Logger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private bool doingActivate;
        private bool doingDeactivate;

        public override Task OnActivateAsync()
        {
            logger = GetLogger();

            var startMe =
                new Task(
                    () =>
                    {
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
                    logger.Info("Started-OnActivateAsync");

                    Assert.True(doingActivate, "Doing Activate");
                    Assert.False(doingDeactivate, "Not doing Deactivate");

                    try
                    {
                        logger.Info("Calling RecordActivateCall");
                        await watcher.RecordActivateCall(Data.ActivationId.ToString());
                        logger.Info("Returned from calling RecordActivateCall");
                    }
                    catch (Exception exc)
                    {
                        var msg = "RecordActivateCall failed with error " + exc;
                        logger.Error(0, msg);
                        Assert.True(false, msg);
                    }

                    Assert.True(doingActivate, "Doing Activate");
                    Assert.False(doingDeactivate, "Not doing Deactivate");

                    await Task.Delay(TimeSpan.FromSeconds(1));

                    doingActivate = false;

                    logger.Info("Finished-OnActivateAsync");
                };
            var awaitMe = startMe.ContinueWith(_ => asyncCont()).Unwrap();
            startMe.Start();
            return awaitMe;
        }

        public override Task OnDeactivateAsync()
        {
            Task.Factory.StartNew(() => logger.Info("OnDeactivateAsync"));

            Assert.False(doingActivate, "Not doing Activate");
            Assert.False(doingDeactivate, "Not doing Deactivate");
            doingDeactivate = true;

            logger.Info("Started-OnDeactivateAsync");
            return watcher.RecordDeactivateCall(Data.ActivationId.ToString())
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
            return Task.FromResult(Data.ActivationId.ToString());
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
        private Logger logger;

        public override Task OnActivateAsync()
        {
            logger = GetLogger();
            logger.Info("OnActivateAsync");
            throw new ApplicationException("Thrown from Application-OnActivateAsync");
        }

        public override Task OnDeactivateAsync()
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
        private Logger logger;

        public BadConstructorTestGrain()
        {
            throw new ApplicationException("Thrown from Constructor");
        }

        public override Task OnActivateAsync()
        {
            logger = GetLogger();
            logger.Info("OnActivateAsync");
            throw new NotImplementedException("OnActivateAsync should not have been called");
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            throw new NotImplementedException("OnDeactivateAsync() should not have been called");
        }

        public Task<string> DoSomething()
        {
            logger.Info("DoSomething");
            throw new NotImplementedException("DoSomething should not have been called");
        }
    }

    internal class DeactivatingWhileActivatingTestGrain : Grain, IDeactivatingWhileActivatingTestGrain
    {
        private Logger logger;

        public override Task OnActivateAsync()
        {
            logger = GetLogger();
            logger.Info("OnActivateAsync");
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
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
        private Logger logger;

        //private IEchoGrain orleansManagedGrain;
        private ITestGrain grain;

        public override Task OnActivateAsync()
        {
            logger = GetLogger();
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
            return Data.ActivationId.ToString();
        }

        public async Task ForwardCall(IBadActivateDeactivateTestGrain otherGrain)
        {
            logger.Info("ForwardCall to " + otherGrain);
            await otherGrain.ThrowSomething();
        }
    }
}
