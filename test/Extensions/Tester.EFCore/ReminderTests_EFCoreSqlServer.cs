using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Internal;
using Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;
using UnitTests.TimerTests;
using UnitTests.GrainInterfaces;

namespace Tester.EFCore;

[TestCategory("Reminders"), TestCategory("EFCore"), TestCategory("EFCore-SqlServer")]
public class ReminderTests_EFCoreSqlServer : ReminderTests_Base, IClassFixture<EFCoreFixture<SqlServerReminderDbContext>>
{
    public ReminderTests_EFCoreSqlServer(EFCoreFixture<SqlServerReminderDbContext> fixture) : base(fixture)
    {
        EFCoreTestUtils.CheckSqlServer();
    }

    // Basic tests
    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_Basic_StopByRef()
    {
        await Test_Reminders_Basic_StopByRef();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_Basic_ListOps()
    {
        await Test_Reminders_Basic_ListOps();
    }

    // Single join tests ... multi grain, multi reminders

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_1J_MultiGrainMultiReminders()
    {
        await Test_Reminders_1J_MultiGrainMultiReminders();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_ReminderNotFound()
    {
        await Test_Reminders_ReminderNotFound();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_Basic()
    {
        // start up a test grain and get the period that it's programmed to use.
        var grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var period = await grain.GetReminderPeriod(DR);
        // start up the 'DR' reminder and wait for two ticks to pass.
        await grain.StartReminder(DR);
        Thread.Sleep(period.Multiply(2) + LEEWAY); // giving some leeway
        // retrieve the value of the counter-- it should match the sequence number which is the number of periods
        // we've waited.
        var last = await grain.GetCounter(DR);
        Assert.Equal(2, last);
        // stop the timer and wait for a whole period.
        await grain.StopReminder(DR);
        Thread.Sleep(period.Multiply(1) + LEEWAY); // giving some leeway
        // the counter should not have changed.
        var curr = await grain.GetCounter(DR);
        Assert.Equal(last, curr);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_Basic_Restart()
    {
        var grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var period = await grain.GetReminderPeriod(DR);
        await grain.StartReminder(DR);
        Thread.Sleep(period.Multiply(2) + LEEWAY); // giving some leeway
        var last = await grain.GetCounter(DR);
        Assert.Equal(2, last);

        await grain.StopReminder(DR);
        var sleepFor = period.Multiply(1) + LEEWAY;
        Thread.Sleep(sleepFor); // giving some leeway
        var curr = await grain.GetCounter(DR);
        Assert.Equal(last, curr);
        AssertIsInRange(curr, last, last + 1, grain, DR, sleepFor);

        // start the same reminder again
        await grain.StartReminder(DR);
        sleepFor = period.Multiply(2) + LEEWAY;
        Thread.Sleep(sleepFor); // giving some leeway
        curr = await grain.GetCounter(DR);
        AssertIsInRange(curr, 2, 3, grain, DR, sleepFor);
        await grain.StopReminder(DR); // cleanup
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_MultipleReminders()
    {
        var grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        await PerGrainMultiReminderTest(grain);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_2J_MultiGrainMultiReminders()
    {
        var g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g2 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g3 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g4 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g5 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

        var period = await g1.GetReminderPeriod(DR);

        Task<bool>[] tasks = {Task.Run(() => PerGrainMultiReminderTestChurn(g1)), Task.Run(() => PerGrainMultiReminderTestChurn(g2)), Task.Run(() => PerGrainMultiReminderTestChurn(g3)), Task.Run(() => PerGrainMultiReminderTestChurn(g4)), Task.Run(() => PerGrainMultiReminderTestChurn(g5)),};

        await Task.Delay(period.Multiply(5));

        // start two extra silos ... although it will take it a while before they stabilize
        log.LogInformation("Starting 2 extra silos");

        await HostedCluster.StartAdditionalSilosAsync(2, true);
        await HostedCluster.WaitForLivenessToStabilizeAsync();

        //Block until all tasks complete.
        await Task.WhenAll(tasks).WithTimeout(ENDWAIT);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_MultiGrainMultiReminders()
    {
        var g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g2 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g3 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g4 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g5 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

        Task<bool>[] tasks = {Task.Run(() => PerGrainMultiReminderTest(g1)), Task.Run(() => PerGrainMultiReminderTest(g2)), Task.Run(() => PerGrainMultiReminderTest(g3)), Task.Run(() => PerGrainMultiReminderTest(g4)), Task.Run(() => PerGrainMultiReminderTest(g5)),};

        //Block until all tasks complete.
        await Task.WhenAll(tasks).WithTimeout(ENDWAIT);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_1F_Basic()
    {
        var g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

        var period = await g1.GetReminderPeriod(DR);

        var test = Task.Run(async () =>
        {
            await PerGrainFailureTest(g1);
            return true;
        });

        Thread.Sleep(period.Multiply(failAfter));
        // stop the secondary silo
        log.LogInformation("Stopping secondary silo");
        await HostedCluster.StopSiloAsync(HostedCluster.SecondarySilos.First());

        await test; // Block until test completes.
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_2F_MultiGrain()
    {
        var silos = await HostedCluster.StartAdditionalSilosAsync(2, true);

        var g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g2 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g3 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g4 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g5 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

        var period = await g1.GetReminderPeriod(DR);

        Task[] tasks = {Task.Run(() => PerGrainFailureTest(g1)), Task.Run(() => PerGrainFailureTest(g2)), Task.Run(() => PerGrainFailureTest(g3)), Task.Run(() => PerGrainFailureTest(g4)), Task.Run(() => PerGrainFailureTest(g5)),};

        Thread.Sleep(period.Multiply(failAfter));

        // stop a couple of silos
        log.LogInformation("Stopping 2 silos");
        var i = Random.Shared.Next(silos.Count);
        await HostedCluster.StopSiloAsync(silos[i]);
        silos.RemoveAt(i);
        await HostedCluster.StopSiloAsync(silos[Random.Shared.Next(silos.Count)]);

        await Task.WhenAll(tasks).WithTimeout(ENDWAIT); // Block until all tasks complete.
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_1F1J_MultiGrain()
    {
        var silos = await HostedCluster.StartAdditionalSilosAsync(1);
        await HostedCluster.WaitForLivenessToStabilizeAsync();

        var g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g2 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g3 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g4 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g5 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

        var period = await g1.GetReminderPeriod(DR);

        Task[] tasks = {Task.Run(() => PerGrainFailureTest(g1)), Task.Run(() => PerGrainFailureTest(g2)), Task.Run(() => PerGrainFailureTest(g3)), Task.Run(() => PerGrainFailureTest(g4)), Task.Run(() => PerGrainFailureTest(g5)),};

        Thread.Sleep(period.Multiply(failAfter));

        var siloToKill = silos[Random.Shared.Next(silos.Count)];
        // stop a silo and join a new one in parallel
        log.LogInformation("Stopping a silo and joining a silo");
        Task t1 = Task.Factory.StartNew(async () => await HostedCluster.StopSiloAsync(siloToKill));
        var t2 = HostedCluster.StartAdditionalSilosAsync(1, true).ContinueWith(t =>
        {
            t.GetAwaiter().GetResult();
        });
        await Task.WhenAll(new[] {t1, t2}).WithTimeout(ENDWAIT);

        await Task.WhenAll(tasks).WithTimeout(ENDWAIT); // Block until all tasks complete.
        log.LogInformation("\n\n\nReminderTest_1F1J_MultiGrain passed OK.\n\n\n");
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_RegisterSameReminderTwice()
    {
        var grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var promise1 = grain.StartReminder(DR);
        var promise2 = grain.StartReminder(DR);
        Task<IGrainReminder>[] tasks = {promise1, promise2};
        await Task.WhenAll(tasks).WithTimeout(TimeSpan.FromSeconds(15));
        //Assert.NotEqual(promise1.Result, promise2.Result);
        // TODO: write tests where period of a reminder is changed
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_GT_Basic()
    {
        var g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g2 = GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
        var period = await g1.GetReminderPeriod(DR); // using same period

        await g1.StartReminder(DR);
        Thread.Sleep(period.Multiply(2) + LEEWAY); // giving some leeway
        await g2.StartReminder(DR);
        Thread.Sleep(period.Multiply(2) + LEEWAY); // giving some leeway
        var last1 = await g1.GetCounter(DR);
        Assert.Equal(4, last1);
        var last2 = await g2.GetCounter(DR);
        Assert.Equal(2, last2); // CopyGrain fault

        await g1.StopReminder(DR);
        Thread.Sleep(period.Multiply(2) + LEEWAY); // giving some leeway
        await g2.StopReminder(DR);
        var curr1 = await g1.GetCounter(DR);
        Assert.Equal(last1, curr1);
        var curr2 = await g2.GetCounter(DR);
        Assert.Equal(4, curr2); // CopyGrain fault
    }

    [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/4319"), TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_GT_1F1J_MultiGrain()
    {
        var silos = await HostedCluster.StartAdditionalSilosAsync(1);
        await HostedCluster.WaitForLivenessToStabilizeAsync();

        var g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g2 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var g3 = GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
        var g4 = GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());

        var period = await g1.GetReminderPeriod(DR);

        Task[] tasks = {Task.Run(() => PerGrainFailureTest(g1)), Task.Run(() => PerGrainFailureTest(g2)), Task.Run(() => PerCopyGrainFailureTest(g3)), Task.Run(() => PerCopyGrainFailureTest(g4)),};

        Thread.Sleep(period.Multiply(failAfter));

        var siloToKill = silos[Random.Shared.Next(silos.Count)];
        // stop a silo and join a new one in parallel
        log.LogInformation("Stopping a silo and joining a silo");
        var t1 = Task.Run(async () => await HostedCluster.StopSiloAsync(siloToKill));
        Task t2 = Task.Run(async () => await HostedCluster.StartAdditionalSilosAsync(1));
        await Task.WhenAll(new[] {t1, t2}).WithTimeout(ENDWAIT);

        await Task.WhenAll(tasks).WithTimeout(ENDWAIT); // Block until all tasks complete.
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_Wrong_LowerThanAllowedPeriod()
    {
        var grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.StartReminder(DR, TimeSpan.FromMilliseconds(3000), true));
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_EFCoreSqlServer_Wrong_Grain()
    {
        var grain = GrainFactory.GetGrain<IReminderGrainWrong>(0);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.StartReminder(DR));
    }
}