﻿using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;

using UnitTestGrainInterfaces;

namespace UnitTestGrains
{
    internal class SimpleActivateDeactivateTestGrain : Grain, ISimpleActivateDeactivateTestGrain
    {
        private readonly Logger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private bool doingActivate;
        private bool doingDeactivate;

        public SimpleActivateDeactivateTestGrain()
        {
            this.logger = GetLogger();
        }

        public override async Task OnActivateAsync()
        {
            logger.Info("OnActivateAsync");
            this.watcher = ActivateDeactivateWatcherGrainFactory.GetGrain(0);
            Assert.IsFalse(doingActivate, "Activate method should have finished");
            Assert.IsFalse(doingDeactivate, "Not doing Deactivate yet");
            doingActivate = true;
            await watcher.RecordActivateCall(this.Data.ActivationId);
            Assert.IsTrue(doingActivate, "Activate method still running");
            doingActivate = false;
        }

        public override async Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            Assert.IsFalse(doingActivate, "Activate method should have finished");
            Assert.IsFalse(doingDeactivate, "Not doing Deactivate yet");
            doingDeactivate = true;
            await watcher.RecordDeactivateCall(this.Data.ActivationId);
            Assert.IsTrue(doingDeactivate, "Deactivate method still running");
            doingDeactivate = false;
        }

        public Task<ActivationId> DoSomething()
        {
            logger.Info("DoSomething");
            Assert.IsFalse(doingActivate, "Activate method should have finished");
            Assert.IsFalse(doingDeactivate, "Deactivate method should not be running yet");
            return Task.FromResult(this.Data.ActivationId);
        }

        public Task DoDeactivate()
        {
            logger.Info("DoDeactivate");
            Assert.IsFalse(doingActivate, "Activate method should have finished");
            Assert.IsFalse(doingDeactivate, "Deactivate method should not be running yet");
            base.DeactivateOnIdle();
            return TaskDone.Done;
        }
    }

    internal class TailCallActivateDeactivateTestGrain : Grain, ITailCallActivateDeactivateTestGrain
    {
        private readonly Logger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private bool doingActivate;
        private bool doingDeactivate;

        public TailCallActivateDeactivateTestGrain()
        {
            this.logger = GetLogger();
        }

        public override Task OnActivateAsync()
        {
            logger.Info("OnActivateAsync");
            this.watcher = ActivateDeactivateWatcherGrainFactory.GetGrain(0);
            Assert.IsFalse(doingActivate, "Activate method should have finished");
            Assert.IsFalse(doingDeactivate, "Not doing Deactivate yet");
            doingActivate = true;
            return watcher.RecordActivateCall(this.Data.ActivationId)
                .ContinueWith((Task t) =>
                {
                    Assert.IsFalse(t.IsFaulted, "RecordActivateCall failed");
                    Assert.IsTrue(doingActivate, "Doing Activate");
                    doingActivate = false;
                });
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            Assert.IsFalse(doingActivate, "Activate method should have finished");
            Assert.IsFalse(doingDeactivate, "Not doing Deactivate yet");
            doingDeactivate = true;
            return watcher.RecordDeactivateCall(this.Data.ActivationId)
                .ContinueWith((Task t) =>
                {
                    Assert.IsFalse(t.IsFaulted, "RecordDeactivateCall failed");
                    Assert.IsTrue(doingDeactivate, "Doing Deactivate");
                    doingDeactivate = false;
                });
        }

        public Task<ActivationId> DoSomething()
        {
            logger.Info("DoSomething");
            Assert.IsFalse(doingActivate, "Activate method should have finished");
            Assert.IsFalse(doingDeactivate, "Deactivate method should not be running yet");
            return Task.FromResult(this.Data.ActivationId);
        }

        public Task DoDeactivate()
        {
            logger.Info("DoDeactivate");
            Assert.IsFalse(doingActivate, "Activate method should have finished");
            Assert.IsFalse(doingDeactivate, "Deactivate method should not be running yet");
            base.DeactivateOnIdle();
            return TaskDone.Done;
        }
    }

    internal class LongRunningActivateDeactivateTestGrain : Grain, ILongRunningActivateDeactivateTestGrain
    {
        private readonly Logger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private bool doingActivate;
        private bool doingDeactivate;

        public LongRunningActivateDeactivateTestGrain()
        {
            this.logger = GetLogger();
        }

        public override async Task OnActivateAsync()
        {
            this.watcher = ActivateDeactivateWatcherGrainFactory.GetGrain(0);

            Assert.IsFalse(doingActivate, "Not doing Activate yet");
            Assert.IsFalse(doingDeactivate, "Not doing Deactivate yet");
            doingActivate = true;

            logger.Info("OnActivateAsync");

            // Spawn Task to run on default .NET thread pool
            Task task = Task.Factory.StartNew(() =>
            {
                logger.Info("Started-OnActivateAsync-SubTask");
                Assert.IsTrue(TaskScheduler.Current == TaskScheduler.Default, "Running under default .NET Task scheduler");
                Assert.IsTrue(doingActivate, "Still doing Activate in Sub-Task");
                logger.Info("Finished-OnActivateAsync-SubTask");
            }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            await task;

            logger.Info("Started-OnActivateAsync");

            await watcher.RecordActivateCall(this.Data.ActivationId);
            Assert.IsTrue(doingActivate, "Doing Activate");

            logger.Info("OnActivateAsync-Sleep");
            Thread.Sleep(TimeSpan.FromSeconds(1));
            Assert.IsTrue(doingActivate, "Still doing Activate after Sleep");

            logger.Info("Finished-OnActivateAsync");
            doingActivate = false;
        }

        public override async Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");

            Assert.IsFalse(doingActivate, "Not doing Activate yet");
            Assert.IsFalse(doingDeactivate, "Not doing Deactivate yet");
            doingDeactivate = true;

            logger.Info("Started-OnDeactivateAsync");

            await watcher.RecordDeactivateCall(this.Data.ActivationId);
            Assert.IsTrue(doingDeactivate, "Doing Deactivate");

            logger.Info("OnDeactivateAsync-Sleep");
            Thread.Sleep(TimeSpan.FromSeconds(1));
            logger.Info("Finished-OnDeactivateAsync");
            doingDeactivate = false;
        }

        public Task<ActivationId> DoSomething()
        {
            logger.Info("DoSomething");
            Assert.IsFalse(doingActivate, "Activate method should have finished");
            Assert.IsFalse(doingDeactivate, "Deactivate method should not be running yet");
            return Task.FromResult(this.Data.ActivationId);
        }

        public Task DoDeactivate()
        {
            logger.Info("DoDeactivate");
            Assert.IsFalse(doingActivate, "Activate method should have finished");
            Assert.IsFalse(doingDeactivate, "Deactivate method should not be running yet");
            base.DeactivateOnIdle();
            return TaskDone.Done;
        }
    }

    internal class TaskActionActivateDeactivateTestGrain : Grain, ITaskActionActivateDeactivateTestGrain
    {
        private readonly Logger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private bool doingActivate;
        private bool doingDeactivate;

        public TaskActionActivateDeactivateTestGrain()
        {
            this.logger = GetLogger();
        }

        public override Task OnActivateAsync()
        {
            var startMe = 
                new Task(
                    () =>
                    {
                        logger.Info("OnActivateAsync");

                        this.watcher = ActivateDeactivateWatcherGrainFactory.GetGrain(0);

                        Assert.IsFalse(doingActivate, "Not doing Activate");
                        Assert.IsFalse(doingDeactivate, "Not doing Deactivate");
                        doingActivate = true;
                    });
            // we want to use Task.ContinueWith with an async lambda, an explicitly typed variable is required to avoid
            // writing code that doesn't do what i think it is doing.
            Func<Task> asyncCont =
                async () =>
                {
                    logger.Info("Started-OnActivateAsync");

                    Assert.IsTrue(doingActivate, "Doing Activate");
                    Assert.IsFalse(doingDeactivate, "Not doing Deactivate");

                    try
                    {
                        logger.Info("Calling RecordActivateCall");
                        await watcher.RecordActivateCall(this.Data.ActivationId);
                        logger.Info("Returned from calling RecordActivateCall");
                    }
                    catch (Exception exc)
                    {
                        string msg = "RecordActivateCall failed with error " + exc;
                        logger.Error(0, msg);
                        Assert.Fail(msg);
                    }

                    Assert.IsTrue(doingActivate, "Doing Activate");
                    Assert.IsFalse(doingDeactivate, "Not doing Deactivate");

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

            Assert.IsFalse(doingActivate, "Not doing Activate");
            Assert.IsFalse(doingDeactivate, "Not doing Deactivate");
            doingDeactivate = true;

            logger.Info("Started-OnDeactivateAsync");
            return watcher.RecordDeactivateCall(this.Data.ActivationId)
            .ContinueWith((Task t) =>
            {
                Assert.IsFalse(t.IsFaulted, "RecordDeactivateCall failed");
                Assert.IsTrue(doingDeactivate, "Doing Deactivate");
                Thread.Sleep(TimeSpan.FromSeconds(1));
                doingDeactivate = false;
            })
            .ContinueWith((Task t) => logger.Info("Finished-OnDeactivateAsync"), TaskContinuationOptions.ExecuteSynchronously);
        }

        public Task<ActivationId> DoSomething()
        {
            logger.Info("DoSomething");
            Assert.IsFalse(doingActivate, "Activate method should have finished");
            Assert.IsFalse(doingDeactivate, "Deactivate method should not be running yet");
            return Task.FromResult(this.Data.ActivationId);
        }

        public Task DoDeactivate()
        {
            logger.Info("DoDeactivate");
            Assert.IsFalse(doingActivate, "Activate method should have finished");
            Assert.IsFalse(doingDeactivate, "Deactivate method should not be running yet");
            base.DeactivateOnIdle();
            return TaskDone.Done;
        }
    }

    public class BadActivateDeactivateTestGrain : Grain, IBadActivateDeactivateTestGrain
    {
        private readonly Logger logger;

        public BadActivateDeactivateTestGrain()
        {
            this.logger = GetLogger();
        }

        public override Task OnActivateAsync()
        {
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
        private readonly Logger logger;

        public BadConstructorTestGrain()
        {
            this.logger = GetLogger();
            throw new ApplicationException("Thrown from Constructor");
        }

        public override Task OnActivateAsync()
        {
            logger.Info("OnActivateAsync");
            throw new NotImplementedException("OnActivateAsync should not have been called");
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            throw new NotImplementedException("OnDeactivateAsync() should not have been called");
        }
        
        public Task<ActivationId> DoSomething()
        {
            logger.Info("DoSomething");
            throw new NotImplementedException("DoSomething should not have been called");
        }
    }

    internal class CreateGrainReferenceTestGrain : Grain, ICreateGrainReferenceTestGrain
    {
        private readonly Logger logger;

        //private IEchoGrain orleansManagedGrain;
        private ISimpleSelfManagedGrain selfManagedGrain;

        public CreateGrainReferenceTestGrain()
        {
            this.logger = GetLogger();
            selfManagedGrain = SimpleSelfManagedGrainFactory.GetGrain(1);
        }

        public override Task OnActivateAsync()
        {
            logger.Info("OnActivateAsync");
            selfManagedGrain = SimpleSelfManagedGrainFactory.GetGrain(1);
            return TaskDone.Done;
        }

        public async Task<ActivationId> DoSomething()
        {
            logger.Info("DoSomething");
            Guid guid = Guid.NewGuid();
            await selfManagedGrain.SetLabel(guid.ToString());
            var label = await selfManagedGrain.GetLabel();

            if (string.IsNullOrEmpty(label))
            {
                throw new ArgumentException("Bad data: Null label returned");
            }
            return this.Data.ActivationId;
        }

        public async Task ForwardCall(IBadActivateDeactivateTestGrain otherGrain)
        {
            logger.Info("ForwardCall to " + otherGrain);
            await otherGrain.ThrowSomething();
        }

    }
}