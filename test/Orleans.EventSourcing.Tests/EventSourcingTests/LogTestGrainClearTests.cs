using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Storage;
using UnitTests.GrainInterfaces;
using Xunit;
using Assert = Xunit.Assert;

namespace Tester.EventSourcingTests
{
    /// <summary>
    /// Integration tests for clear-log behavior on non-Azure log test grain configurations.
    /// </summary>
    public class LogTestGrainClearTests : IClassFixture<EventSourcingClusterFixture>
    {
        private readonly EventSourcingClusterFixture fixture;

        public LogTestGrainClearTests(EventSourcingClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [Theory, TestCategory("EventSourcing"), TestCategory("Functional")]
        [InlineData("TestGrains.LogTestGrainDefaultStorage", 721001L)]
        [InlineData("TestGrains.LogTestGrainSharedLogStorage", 721002L)]
        [InlineData("TestGrains.LogTestGrainCustomStoragePrimaryCluster", 721003L)]
        [InlineData("TestGrains.LogTestGrainJournaledStateStorage", 721004L)]
        public async Task ClearLog_ResetDropsTentativeAndAllowsFurtherWrites(string grainClass, long grainId)
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(grainId, grainClass);

            await grain.Clear();
            await grain.SetAGlobal(10);
            Assert.Equal(10, await grain.GetAGlobal());
            Assert.Equal(1, await grain.GetConfirmedVersion());

            await grain.SetALocal(99);
            await grain.SetBLocal(77);
            var tentativeBeforeClear = await grain.GetBothLocal();
            Assert.Equal(99, tentativeBeforeClear.A);
            Assert.Equal(77, tentativeBeforeClear.B);

            await grain.Clear();
            Assert.Equal(0, await grain.GetConfirmedVersion());

            var confirmedAfterClear = await grain.GetBothGlobal();
            Assert.Equal(0, confirmedAfterClear.A);
            Assert.Equal(0, confirmedAfterClear.B);

            var tentativeAfterClear = await grain.GetBothLocal();
            Assert.Equal(0, tentativeAfterClear.A);
            Assert.Equal(0, tentativeAfterClear.B);

            await grain.SetAGlobal(41);
            await grain.IncrementAGlobal();
            Assert.Equal(42, await grain.GetAGlobal());
            Assert.Equal(2, await grain.GetConfirmedVersion());

            await grain.Clear();
            var exceptions = await RunConcurrentOperationsAroundClear(grain);
            Assert.DoesNotContain(exceptions, static ex => ex is InconsistentStateException);
            Assert.Empty(exceptions);

            await grain.Clear();
            await grain.SetAGlobal(7);
            Assert.Equal(7, await grain.GetAGlobal());
            Assert.Equal(1, await grain.GetConfirmedVersion());
        }

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task JournaledStateLogStorage_PersistsEventsAcrossActivation()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(721005L, "TestGrains.LogTestGrainJournaledStateStorage");

            await grain.Clear();
            await grain.SetAGlobal(10);
            await grain.IncrementAGlobal();

            var eventLog = await grain.GetEventLog();
            Assert.Equal(2, eventLog.Count);
            Assert.Equal(11, await grain.GetAGlobal());

            await grain.Deactivate();
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(721005L, "TestGrains.LogTestGrainJournaledStateStorage");
            Assert.Equal(11, await grain.GetAGlobal());
            Assert.Equal(2, await grain.GetConfirmedVersion());

            eventLog = await grain.GetEventLog();
            Assert.Equal(2, eventLog.Count);
        }

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task JournaledStateLogStorage_ClearPreservesOtherJournaledState()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721006L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");

            await grain.Clear();
            await grain.SetAuxiliaryValue(17);
            await grain.SetAGlobal(10);

            Assert.Equal(17, await grain.GetAuxiliaryValue());
            Assert.Equal(10, await grain.GetAGlobal());
            Assert.Equal(1, await grain.GetConfirmedVersion());

            await grain.Clear();

            Assert.Equal(17, await grain.GetAuxiliaryValue());
            Assert.Equal(0, await grain.GetConfirmedVersion());
            Assert.Equal(0, await grain.GetAGlobal());

            await grain.Deactivate();
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721006L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");

            Assert.Equal(17, await grain.GetAuxiliaryValue());
            Assert.Equal(0, await grain.GetConfirmedVersion());
            Assert.Equal(0, await grain.GetAGlobal());

            await grain.SetAGlobal(42);
            Assert.Equal(17, await grain.GetAuxiliaryValue());
            Assert.Equal(1, await grain.GetConfirmedVersion());
        }

        private static async Task<List<Exception>> RunConcurrentOperationsAroundClear(ILogTestGrain grain)
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var exceptions = new List<Exception>();
            var syncLock = new object();

            Task Run(Func<Task> operation)
            {
                return Task.Run(async () =>
                {
                    await gate.Task;
                    try
                    {
                        await operation();
                    }
                    catch (Exception exception)
                    {
                        lock (syncLock)
                        {
                            exceptions.Add(exception);
                        }
                    }
                });
            }

            var operations = new[]
            {
                Run(() => grain.SetALocal(1)),
                Run(() => grain.SetAGlobal(2)),
                Run(() => grain.IncrementAGlobal()),
                Run(() => grain.Clear()),
                Run(() => grain.SetAGlobal(3)),
                Run(async () => _ = await grain.GetAGlobal()),
            };

            gate.SetResult(true);
            await Task.WhenAll(operations);
            return exceptions;
        }
    }
}
